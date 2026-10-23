package app.nexscreen.core.input

import app.nexscreen.core.network.InputMessage
import app.nexscreen.core.network.PointerEvent
import app.nexscreen.core.network.ScrollEvent
import kotlin.math.abs
import kotlin.math.hypot

/** One finger in a [TouchSample], in view pixels. */
data class TouchPointer(val id: Int, val x: Float, val y: Float)

enum class TouchAction {
    /** First finger down. */
    DOWN,

    /** Another finger down while at least one is already down. */
    POINTER_DOWN,
    MOVE,

    /** A finger up while others stay down. */
    POINTER_UP,

    /** Last finger up. */
    UP,
    CANCEL,
}

/**
 * A platform-neutral copy of a touch event: [pointers] are all fingers currently down (for the up actions, the
 * lifting finger is still included), [actionPointerId] is the finger the action is about.
 */
data class TouchSample(
    val action: TouchAction,
    val actionPointerId: Int,
    val pointers: List<TouchPointer>,
    val timeUs: Long,
)

data class GestureConfig(
    /** Movement (view px) after which a touch becomes a drag or a multi-finger tap is no longer a tap. */
    val touchSlopPx: Float = 16f,
    /** Holding still this long without moving is a right click. */
    val longPressUs: Long = 500_000,
    /** Minimum spacing of MOVE and SCROLL messages: 8334 us keeps them at or below 120 Hz. */
    val minSendIntervalUs: Long = 8_334,
    /** Two-finger travel (view px) that equals one wheel notch (120 units). */
    val scrollPxPerNotch: Float = 80f,
    /** A three-finger tap must end within this time. */
    val multiTapMaxUs: Long = 500_000,
)

/** What a touch event turned into. */
sealed interface GestureOutput {
    /** A message for the companion. */
    data class Send(val message: InputMessage) : GestureOutput

    /** Three fingers tapped: a local UI gesture (toggles the stats panel), not sent to Windows. */
    data object ThreeFingerTap : GestureOutput
}

/**
 * Turns touches on the video into NexLink POINTER and SCROLL messages. Pure Kotlin: the caller feeds [onTouch]
 * and calls [onTimer] at [nextDeadlineUs] (for the long-press and for the last throttled move).
 *
 * Gestures (all start on the video; a touch that starts on a letterbox bar is ignored until every finger is up):
 * - tap: left click where the finger was;
 * - one finger moving more than the touch slop: left-button drag from the down point, following the finger;
 * - one finger held still for [GestureConfig.longPressUs]: right click at that point;
 * - two fingers: scroll wheel (vertical and horizontal), content follows the fingers like a touch screen;
 * - three fingers tapped: [GestureOutput.ThreeFingerTap] (local).
 *
 * Nothing is sent on finger down: the click is decided when the finger lifts, moves or is held.
 */
class GestureInterpreter(
    private val config: GestureConfig = GestureConfig(),
    private val videoRect: () -> VideoRect?,
) {
    private sealed interface State {
        data object Idle : State

        /** Started outside the video, or a gesture already finished: wait for all fingers up. */
        data object Ignoring : State

        /** One finger down, not yet a tap, drag or long-press. */
        data class Pending(val id: Int, val x: Float, val y: Float, val timeUs: Long) : State

        data class Dragging(val id: Int) : State

        data class Scrolling(val firstId: Int, val secondId: Int, var lastX: Float, var lastY: Float) : State

        data class MultiTap(val startUs: Long, val start: MutableMap<Int, TouchPointer>, var maxFingers: Int, var moved: Boolean) : State
    }

    private var state: State = State.Idle
    private var rect: VideoRect? = null
    private var wireId = 0

    // Throttling of moves and scrolls
    private var lastSendUs = Long.MIN_VALUE / 4 // far in the past without overflowing subtractions
    private var pendingMove: PointerEvent? = null
    private var scrollAccX = 0f
    private var scrollAccY = 0f
    private var lastPointer: PointerEvent? = null

    /** When [onTimer] must be called next, or null when nothing is waiting. */
    fun nextDeadlineUs(): Long? {
        val s = state
        return when {
            s is State.Pending -> s.timeUs + config.longPressUs
            pendingMove != null || hasScrollToSend() -> lastSendUs + config.minSendIntervalUs
            else -> null
        }
    }

    fun onTimer(nowUs: Long): List<GestureOutput> {
        val out = ArrayList<GestureOutput>(2)
        val s = state
        if (s is State.Pending && nowUs - s.timeUs >= config.longPressUs) {
            rightClick(s, out)
        }
        if (nowUs - lastSendUs >= config.minSendIntervalUs) {
            flushMove(nowUs, out)
            flushScroll(nowUs, out)
        }
        return out
    }

    fun onTouch(sample: TouchSample): List<GestureOutput> {
        val out = ArrayList<GestureOutput>(2)
        when (sample.action) {
            TouchAction.DOWN -> onFirstDown(sample)
            TouchAction.POINTER_DOWN -> onExtraDown(sample, out)
            TouchAction.MOVE -> onMove(sample, out)
            TouchAction.POINTER_UP -> onPointerUp(sample, out)
            TouchAction.UP -> onLastUp(sample, out)
            TouchAction.CANCEL -> onCancel(out)
        }
        return out
    }

    private fun onFirstDown(sample: TouchSample) {
        reset()
        val p = sample.pointer(sample.actionPointerId) ?: return
        val r = videoRect()
        if (r == null || !r.contains(p.x, p.y)) {
            state = State.Ignoring
            return
        }
        rect = r
        wireId = p.id and 0xFF
        state = State.Pending(p.id, p.x, p.y, sample.timeUs)
    }

    private fun onExtraDown(sample: TouchSample, out: MutableList<GestureOutput>) {
        when (val s = state) {
            is State.Pending -> {
                if (sample.pointers.size >= 3) {
                    state = multiTap(sample)
                } else {
                    val second = sample.pointers.firstOrNull { it.id != s.id } ?: return
                    val (cx, cy) = centroid(sample.pointer(s.id) ?: return, second)
                    state = State.Scrolling(s.id, second.id, cx, cy)
                    // Windows scrolls the window under the cursor, so park the cursor between the fingers
                    sendNow(pointer(PointerEvent.ACTION_MOVE, 0, cx, cy), sample.timeUs, out)
                }
            }
            is State.Scrolling -> if (sample.pointers.size >= 3) state = multiTap(sample)
            is State.MultiTap -> {
                s.maxFingers = maxOf(s.maxFingers, sample.pointers.size)
                sample.pointer(sample.actionPointerId)?.let { s.start.putIfAbsent(it.id, it) }
            }
            else -> Unit // a drag keeps its single finger; extra fingers are ignored
        }
    }

    private fun onMove(sample: TouchSample, out: MutableList<GestureOutput>) {
        when (val s = state) {
            is State.Pending -> {
                val p = sample.pointer(s.id) ?: return
                if (hypot(p.x - s.x, p.y - s.y) > config.touchSlopPx) {
                    // Press where the finger went down, then follow it
                    state = State.Dragging(s.id)
                    sendNow(pointer(PointerEvent.ACTION_DOWN, PointerEvent.BUTTON_LEFT, s.x, s.y), sample.timeUs, out)
                    queueMove(pointer(PointerEvent.ACTION_MOVE, PointerEvent.BUTTON_LEFT, p.x, p.y), sample.timeUs, out)
                }
            }
            is State.Dragging -> {
                val p = sample.pointer(s.id) ?: return
                queueMove(pointer(PointerEvent.ACTION_MOVE, PointerEvent.BUTTON_LEFT, p.x, p.y), sample.timeUs, out)
            }
            is State.Scrolling -> {
                val a = sample.pointer(s.firstId) ?: return
                val b = sample.pointer(s.secondId) ?: return
                val (cx, cy) = centroid(a, b)
                val perPx = ScrollEvent.WHEEL_DELTA / config.scrollPxPerNotch
                // Content follows the fingers: fingers down = wheel up (positive), fingers left = scroll right (positive)
                scrollAccY += (cy - s.lastY) * perPx
                scrollAccX += (s.lastX - cx) * perPx
                s.lastX = cx
                s.lastY = cy
                if (sample.timeUs - lastSendUs >= config.minSendIntervalUs) flushScroll(sample.timeUs, out)
            }
            is State.MultiTap -> {
                for (p in sample.pointers) {
                    val start = s.start[p.id] ?: continue
                    if (hypot(p.x - start.x, p.y - start.y) > config.touchSlopPx * 2) s.moved = true
                }
            }
            else -> Unit
        }
    }

    private fun onPointerUp(sample: TouchSample, out: MutableList<GestureOutput>) {
        when (val s = state) {
            is State.Dragging -> if (sample.actionPointerId == s.id) {
                release(sample, s.id, out)
                state = State.Ignoring
            }
            is State.Scrolling -> {
                flushScroll(sample.timeUs, out, force = true)
                state = State.Ignoring
            }
            is State.Pending -> if (sample.actionPointerId == s.id) state = State.Ignoring
            else -> Unit
        }
    }

    private fun onLastUp(sample: TouchSample, out: MutableList<GestureOutput>) {
        when (val s = state) {
            is State.Pending -> {
                if (sample.timeUs - s.timeUs >= config.longPressUs) {
                    rightClick(s, out) // the timer was late
                } else {
                    sendNow(pointer(PointerEvent.ACTION_DOWN, PointerEvent.BUTTON_LEFT, s.x, s.y), sample.timeUs, out)
                    sendNow(pointer(PointerEvent.ACTION_UP, 0, s.x, s.y), sample.timeUs, out)
                }
            }
            is State.Dragging -> release(sample, s.id, out)
            is State.Scrolling -> flushScroll(sample.timeUs, out, force = true)
            is State.MultiTap -> if (!s.moved && s.maxFingers == 3 && sample.timeUs - s.startUs <= config.multiTapMaxUs) {
                out += GestureOutput.ThreeFingerTap
            }
            else -> Unit
        }
        reset()
    }

    private fun onCancel(out: MutableList<GestureOutput>) {
        if (state is State.Dragging) {
            val last = pendingMove ?: lastPointer
            pendingMove = null
            out += GestureOutput.Send(PointerEvent(PointerEvent.ACTION_CANCEL, wireId, 0, last?.xNorm ?: 0, last?.yNorm ?: 0))
        }
        reset()
    }

    private fun rightClick(s: State.Pending, out: MutableList<GestureOutput>) {
        sendNow(pointer(PointerEvent.ACTION_DOWN, PointerEvent.BUTTON_RIGHT, s.x, s.y), s.timeUs + config.longPressUs, out)
        sendNow(pointer(PointerEvent.ACTION_UP, 0, s.x, s.y), s.timeUs + config.longPressUs, out)
        state = State.Ignoring
    }

    private fun release(sample: TouchSample, id: Int, out: MutableList<GestureOutput>) {
        val p = sample.pointer(id)
        val up = if (p != null) pointer(PointerEvent.ACTION_UP, 0, p.x, p.y) else lastPointer!!.copy(action = PointerEvent.ACTION_UP, buttons = 0)
        pendingMove = null // the up carries the final position
        sendNow(up, sample.timeUs, out)
    }

    private fun queueMove(event: PointerEvent, timeUs: Long, out: MutableList<GestureOutput>) {
        if (timeUs - lastSendUs >= config.minSendIntervalUs) {
            pendingMove = null
            sendNow(event, timeUs, out)
        } else {
            pendingMove = event
        }
    }

    private fun flushMove(nowUs: Long, out: MutableList<GestureOutput>) {
        val move = pendingMove ?: return
        pendingMove = null
        sendNow(move, nowUs, out)
    }

    private fun hasScrollToSend(): Boolean = abs(scrollAccX) >= 1f || abs(scrollAccY) >= 1f

    private fun flushScroll(nowUs: Long, out: MutableList<GestureOutput>, force: Boolean = false) {
        if (!hasScrollToSend()) return
        if (!force && nowUs - lastSendUs < config.minSendIntervalUs) return
        val dx = scrollAccX.toInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
        val dy = scrollAccY.toInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
        scrollAccX -= dx
        scrollAccY -= dy
        lastSendUs = nowUs
        out += GestureOutput.Send(ScrollEvent(dx, dy))
    }

    private fun sendNow(event: PointerEvent, timeUs: Long, out: MutableList<GestureOutput>) {
        lastSendUs = timeUs
        lastPointer = event
        out += GestureOutput.Send(event)
    }

    private fun pointer(action: Int, buttons: Int, x: Float, y: Float): PointerEvent {
        val r = rect!!
        return PointerEvent(action, wireId, buttons, r.normX(x), r.normY(y))
    }

    private fun multiTap(sample: TouchSample) =
        State.MultiTap(sample.timeUs, sample.pointers.associateBy { it.id }.toMutableMap(), sample.pointers.size, false)

    private fun reset() {
        state = State.Idle
        pendingMove = null
        scrollAccX = 0f
        scrollAccY = 0f
    }

    private fun centroid(a: TouchPointer, b: TouchPointer) = Pair((a.x + b.x) / 2f, (a.y + b.y) / 2f)

    private fun TouchSample.pointer(id: Int): TouchPointer? = pointers.firstOrNull { it.id == id }
}
