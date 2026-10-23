package app.nexscreen.core.input

import kotlin.math.roundToInt

/**
 * Where the video is drawn, in the same coordinate space as the touch samples (after removing the letterbox
 * bars). Maps view coordinates to the protocol's 0..65535 normalized video coordinates.
 */
data class VideoRect(val left: Float, val top: Float, val width: Float, val height: Float) {
    val right: Float get() = left + width
    val bottom: Float get() = top + height
    val isEmpty: Boolean get() = width <= 0f || height <= 0f

    /** True when the point lies on the video (edges included), i.e. not on a letterbox bar. */
    fun contains(x: Float, y: Float): Boolean = !isEmpty && x >= left && x <= right && y >= top && y <= bottom

    /** Normalized X, clamped to the video so a drag that leaves it sticks to the edge. */
    fun normX(x: Float): Int = normalize((x - left) / width)

    fun normY(y: Float): Int = normalize((y - top) / height)

    companion object {
        const val MAX_NORM = 0xFFFF

        /**
         * The largest rectangle with the video's aspect ratio centered in a [viewWidth] x [viewHeight] view, which
         * is how the app letterboxes the stream.
         */
        fun letterbox(viewWidth: Float, viewHeight: Float, videoWidth: Int, videoHeight: Int): VideoRect {
            if (viewWidth <= 0f || viewHeight <= 0f || videoWidth <= 0 || videoHeight <= 0) {
                return VideoRect(0f, 0f, viewWidth.coerceAtLeast(0f), viewHeight.coerceAtLeast(0f))
            }
            val scale = minOf(viewWidth / videoWidth, viewHeight / videoHeight)
            val w = videoWidth * scale
            val h = videoHeight * scale
            return VideoRect((viewWidth - w) / 2f, (viewHeight - h) / 2f, w, h)
        }

        private fun normalize(fraction: Float): Int {
            if (fraction.isNaN()) return 0
            return (fraction.coerceIn(0f, 1f) * MAX_NORM).roundToInt().coerceIn(0, MAX_NORM)
        }
    }
}
