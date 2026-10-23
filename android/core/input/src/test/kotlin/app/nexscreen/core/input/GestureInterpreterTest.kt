package app.nexscreen.core.input

import app.nexscreen.core.network.PointerEvent
import app.nexscreen.core.network.PointerEvent.Companion.ACTION_CANCEL
import app.nexscreen.core.network.PointerEvent.Companion.ACTION_DOWN
import app.nexscreen.core.network.PointerEvent.Companion.ACTION_MOVE
import app.nexscreen.core.network.PointerEvent.Companion.ACTION_UP
import app.nexscreen.core.network.PointerEvent.Companion.BUTTON_LEFT
import app.nexscreen.core.network.PointerEvent.Companion.BUTTON_RIGHT
import app.nexscreen.core.network.ScrollEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class GestureInterpreterTest {

    // A 1920x1200 tablet showing a 1728x1080 (16:10) stream fills the screen: no bars
    private val fullScreen = VideoRect(0f, 0f, 1920f, 1200f)

    private var rect: VideoRect? = fullScreen
    private val interpreter = GestureInterpreter(GestureConfig()) { rect }
    private val sent = ArrayList<GestureOutput>()

    private fun touch(action: TouchAction, timeMs: Long, vararg pointers: TouchPointer, actionId: Int = pointers.first().id) {
        sent += interpreter.onTouch(TouchSample(action, actionId, pointers.toList(), timeMs * 1_000))
    }

    private fun timer(timeMs: Long) {
        sent += interpreter.onTimer(timeMs * 1_000)
    }

    private fun p(x: Float, y: Float, id: Int = 0) = TouchPointer(id, x, y)

    private fun pointers() = sent.filterIsInstance<GestureOutput.Send>().map { it.message }.filterIsInstance<PointerEvent>()

    private fun scrolls() = sent.filterIsInstance<GestureOutput.Send>().map { it.message }.filterIsInstance<ScrollEvent>()

    @Test
    fun tapIsLeftClick() {
        touch(TouchAction.DOWN, 0, p(960f, 600f))
        assertTrue("nothing is sent on down", sent.isEmpty())
        touch(TouchAction.MOVE, 30, p(963f, 602f)) // within the slop
        touch(TouchAction.UP, 80, p(963f, 602f))

        val x = Math.round(960f / 1920f * 65535)
        val y = Math.round(600f / 1200f * 65535)
        assertEquals(
            listOf(PointerEvent(ACTION_DOWN, 0, BUTTON_LEFT, x, y), PointerEvent(ACTION_UP, 0, 0, x, y)),
            pointers(),
        )
        assertNull(interpreter.nextDeadlineUs())
    }

    @Test
    fun dragHoldsLeftButtonFromDownPoint() {
        touch(TouchAction.DOWN, 0, p(100f, 100f))
        touch(TouchAction.MOVE, 20, p(140f, 100f))
        touch(TouchAction.MOVE, 40, p(200f, 150f))
        touch(TouchAction.UP, 60, p(210f, 160f))

        val events = pointers()
        assertEquals(PointerEvent(ACTION_DOWN, 0, BUTTON_LEFT, fullScreen.normX(100f), fullScreen.normY(100f)), events.first())
        assertEquals(PointerEvent(ACTION_UP, 0, 0, fullScreen.normX(210f), fullScreen.normY(160f)), events.last())
        val moves = events.filter { it.action == ACTION_MOVE }
        assertTrue(moves.isNotEmpty())
        assertTrue(moves.all { it.buttons == BUTTON_LEFT })
        assertEquals(fullScreen.normX(200f), moves.last().xNorm)
    }

    @Test
    fun movesAreThrottledTo120Hz() {
        touch(TouchAction.DOWN, 0, p(100f, 100f))
        touch(TouchAction.MOVE, 100, p(200f, 100f)) // starts the drag: DOWN + MOVE
        for (t in 101..200) touch(TouchAction.MOVE, t.toLong(), p(200f + t, 100f)) // 1 kHz input
        val moves = pointers().filter { it.action == ACTION_MOVE }
        // ~100 ms at <= 120 Hz: at most 12 more moves after the first
        assertTrue("got ${moves.size} moves", moves.size in 10..13)

        // The last throttled position is not lost: the timer flushes it
        val deadline = interpreter.nextDeadlineUs()!!
        sent += interpreter.onTimer(deadline)
        assertEquals(fullScreen.normX(400f), pointers().last().xNorm)
        assertEquals(ACTION_MOVE, pointers().last().action)
    }

    @Test
    fun longPressIsRightClick() {
        touch(TouchAction.DOWN, 0, p(500f, 400f))
        assertEquals(500_000L, interpreter.nextDeadlineUs())
        timer(499)
        assertTrue(sent.isEmpty())
        timer(500)
        val x = fullScreen.normX(500f)
        val y = fullScreen.normY(400f)
        assertEquals(listOf(PointerEvent(ACTION_DOWN, 0, BUTTON_RIGHT, x, y), PointerEvent(ACTION_UP, 0, 0, x, y)), pointers())

        // Moving or lifting afterwards does nothing more
        touch(TouchAction.MOVE, 600, p(700f, 400f))
        touch(TouchAction.UP, 700, p(700f, 400f))
        assertEquals(2, pointers().size)
    }

    @Test
    fun lateTimerStillGivesRightClickOnUp() {
        touch(TouchAction.DOWN, 0, p(500f, 400f))
        touch(TouchAction.UP, 650, p(500f, 400f))
        assertEquals(listOf(BUTTON_RIGHT, 0), pointers().map { it.buttons })
    }

    @Test
    fun movingBeforeLongPressCancelsIt() {
        touch(TouchAction.DOWN, 0, p(500f, 400f))
        touch(TouchAction.MOVE, 300, p(560f, 400f))
        timer(500)
        assertTrue(pointers().none { it.buttons and BUTTON_RIGHT != 0 })
        assertEquals(BUTTON_LEFT, pointers().first().buttons)
    }

    @Test
    fun twoFingerScrollVerticalAndHorizontal() {
        touch(TouchAction.DOWN, 0, p(800f, 500f, id = 0))
        touch(TouchAction.POINTER_DOWN, 10, p(800f, 500f, id = 0), p(1000f, 500f, id = 1), actionId = 1)
        // Cursor parked between the fingers, no button
        assertEquals(PointerEvent(ACTION_MOVE, 0, 0, fullScreen.normX(900f), fullScreen.normY(500f)), pointers().single())

        // Fingers move down 80 px (one notch): content follows, so the wheel scrolls up (positive)
        touch(TouchAction.MOVE, 30, p(800f, 540f, id = 0), p(1000f, 540f, id = 1))
        touch(TouchAction.MOVE, 50, p(800f, 580f, id = 0), p(1000f, 580f, id = 1))
        assertEquals(120, scrolls().sumOf { it.dy })
        assertEquals(0, scrolls().sumOf { it.dx })

        // Fingers move left 40 px: half a notch to the right (positive dx)
        touch(TouchAction.MOVE, 70, p(760f, 580f, id = 0), p(960f, 580f, id = 1))
        touch(TouchAction.POINTER_UP, 90, p(760f, 580f, id = 0), p(960f, 580f, id = 1), actionId = 1)
        touch(TouchAction.UP, 100, p(760f, 580f, id = 0))
        assertEquals(60, scrolls().sumOf { it.dx })
        assertEquals(120, scrolls().sumOf { it.dy })
        // Never clicks
        assertTrue(pointers().all { it.buttons == 0 })
    }

    @Test
    fun scrollUpwardsIsNegative() {
        touch(TouchAction.DOWN, 0, p(800f, 500f, id = 0))
        touch(TouchAction.POINTER_DOWN, 5, p(800f, 500f, id = 0), p(1000f, 500f, id = 1), actionId = 1)
        touch(TouchAction.MOVE, 20, p(800f, 340f, id = 0), p(1000f, 340f, id = 1))
        touch(TouchAction.UP, 40, p(800f, 340f, id = 0))
        assertEquals(-240, scrolls().sumOf { it.dy })
    }

    @Test
    fun letterboxMappingAndBarsAreIgnored() {
        // A 1728x1080 stream on a 1200x1920 portrait screen: 1200x750 video centered vertically (bars of 585 px)
        val letterbox = VideoRect.letterbox(1200f, 1920f, 1728, 1080)
        assertEquals(VideoRect(0f, 585f, 1200f, 750f), letterbox)
        rect = letterbox

        // Tap on the top bar: ignored
        touch(TouchAction.DOWN, 0, p(600f, 300f))
        touch(TouchAction.UP, 50, p(600f, 300f))
        timer(1_000)
        assertTrue(sent.isEmpty())

        // Tap on the video's top-left and bottom-right corners
        touch(TouchAction.DOWN, 2_000, p(0f, 585f))
        touch(TouchAction.UP, 2_050, p(0f, 585f))
        touch(TouchAction.DOWN, 3_000, p(1200f, 1335f))
        touch(TouchAction.UP, 3_050, p(1200f, 1335f))
        // Center
        touch(TouchAction.DOWN, 4_000, p(600f, 960f))
        touch(TouchAction.UP, 4_050, p(600f, 960f))
        val downs = pointers().filter { it.action == ACTION_DOWN }
        assertEquals(listOf(0 to 0, 65535 to 65535, 32768 to 32768), downs.map { it.xNorm to it.yNorm })
    }

    @Test
    fun dragLeavingTheVideoIsClampedToItsEdge() {
        rect = VideoRect.letterbox(1200f, 1920f, 1728, 1080)
        touch(TouchAction.DOWN, 0, p(600f, 700f))
        touch(TouchAction.MOVE, 50, p(600f, 100f)) // into the top bar
        touch(TouchAction.UP, 100, p(600f, 100f))
        assertEquals(0, pointers().last().yNorm)
    }

    @Test
    fun noVideoMeansNoInput() {
        rect = null
        touch(TouchAction.DOWN, 0, p(600f, 600f))
        touch(TouchAction.UP, 50, p(600f, 600f))
        assertTrue(sent.isEmpty())
    }

    @Test
    fun cancelDuringDragReleasesTheButton() {
        touch(TouchAction.DOWN, 0, p(100f, 100f))
        touch(TouchAction.MOVE, 20, p(200f, 100f))
        touch(TouchAction.CANCEL, 30, p(200f, 100f))
        assertEquals(ACTION_CANCEL, pointers().last().action)
        assertEquals(0, pointers().last().buttons)
    }

    @Test
    fun threeFingerTapIsLocal() {
        touch(TouchAction.DOWN, 0, p(500f, 500f, id = 0))
        touch(TouchAction.POINTER_DOWN, 10, p(500f, 500f, id = 0), p(700f, 500f, id = 1), actionId = 1)
        touch(TouchAction.POINTER_DOWN, 20, p(500f, 500f, id = 0), p(700f, 500f, id = 1), p(900f, 500f, id = 2), actionId = 2)
        touch(TouchAction.POINTER_UP, 100, p(500f, 500f, id = 0), p(700f, 500f, id = 1), p(900f, 500f, id = 2), actionId = 2)
        touch(TouchAction.POINTER_UP, 110, p(500f, 500f, id = 0), p(700f, 500f, id = 1), actionId = 1)
        touch(TouchAction.UP, 120, p(500f, 500f, id = 0))
        assertEquals(1, sent.count { it == GestureOutput.ThreeFingerTap })
        assertTrue(pointers().none { it.buttons != 0 })
        assertTrue(scrolls().isEmpty())
    }

    @Test
    fun extraFingerDuringDragIsIgnored() {
        touch(TouchAction.DOWN, 0, p(100f, 100f, id = 0))
        touch(TouchAction.MOVE, 20, p(200f, 100f, id = 0))
        touch(TouchAction.POINTER_DOWN, 40, p(200f, 100f, id = 0), p(900f, 900f, id = 1), actionId = 1)
        touch(TouchAction.MOVE, 60, p(250f, 100f, id = 0), p(950f, 900f, id = 1))
        touch(TouchAction.POINTER_UP, 80, p(250f, 100f, id = 0), p(950f, 900f, id = 1), actionId = 0)
        touch(TouchAction.UP, 100, p(950f, 900f, id = 1))
        val events = pointers()
        assertEquals(1, events.count { it.action == ACTION_UP })
        assertEquals(fullScreen.normX(250f), events.last().xNorm)
        assertTrue(scrolls().isEmpty())
    }
}
