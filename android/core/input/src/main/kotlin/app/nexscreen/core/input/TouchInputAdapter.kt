package app.nexscreen.core.input

import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.MotionEvent
import app.nexscreen.core.network.InputMessage

/**
 * Thin Android side of [GestureInterpreter]: converts [MotionEvent]s (in the coordinate space of [videoRect]) to
 * [TouchSample]s and runs the interpreter's timers on the main looper. Must be used from the main thread.
 */
class TouchInputAdapter(
    videoRect: () -> VideoRect?,
    private val send: (InputMessage) -> Unit,
    private val onThreeFingerTap: () -> Unit,
    config: GestureConfig = GestureConfig(),
) {
    private val interpreter = GestureInterpreter(config, videoRect)
    private val handler = Handler(Looper.getMainLooper())
    private val timer = Runnable { deliver(interpreter.onTimer(nowUs())) }

    /** Feeds one event. Always consumes it so the gesture keeps reaching this adapter. */
    fun onTouchEvent(event: MotionEvent): Boolean {
        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> TouchAction.DOWN
            MotionEvent.ACTION_POINTER_DOWN -> TouchAction.POINTER_DOWN
            MotionEvent.ACTION_MOVE -> TouchAction.MOVE
            MotionEvent.ACTION_POINTER_UP -> TouchAction.POINTER_UP
            MotionEvent.ACTION_UP -> TouchAction.UP
            MotionEvent.ACTION_CANCEL -> TouchAction.CANCEL
            else -> return true
        }
        val pointers = List(event.pointerCount) { i -> TouchPointer(event.getPointerId(i), event.getX(i), event.getY(i)) }
        val sample = TouchSample(action, event.getPointerId(event.actionIndex), pointers, eventTimeUs(event))
        deliver(interpreter.onTouch(sample))
        return true
    }

    /** Drops any gesture in progress (e.g. when the view goes away). */
    fun cancel() {
        val now = SystemClock.uptimeMillis()
        val event = MotionEvent.obtain(now, now, MotionEvent.ACTION_CANCEL, 0f, 0f, 0)
        onTouchEvent(event)
        event.recycle()
        handler.removeCallbacks(timer)
    }

    private fun deliver(outputs: List<GestureOutput>) {
        for (o in outputs) {
            when (o) {
                is GestureOutput.Send -> send(o.message)
                GestureOutput.ThreeFingerTap -> onThreeFingerTap()
            }
        }
        handler.removeCallbacks(timer)
        val deadline = interpreter.nextDeadlineUs() ?: return
        val delayMs = ((deadline - nowUs()) + 999) / 1000
        handler.postDelayed(timer, delayMs.coerceAtLeast(0))
    }

    // Event times and the timer share the uptime clock
    private fun eventTimeUs(event: MotionEvent): Long =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) event.eventTimeNanos / 1_000 else event.eventTime * 1_000

    // System.nanoTime() is CLOCK_MONOTONIC on Android, the clock behind SystemClock.uptimeMillis() and event times
    private fun nowUs(): Long = System.nanoTime() / 1_000
}
