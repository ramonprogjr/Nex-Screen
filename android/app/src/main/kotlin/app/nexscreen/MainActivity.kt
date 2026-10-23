package app.nexscreen

import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.layout.boundsInParent
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import app.nexscreen.core.input.TouchInputAdapter
import app.nexscreen.core.input.VideoRect
import app.nexscreen.core.network.ConnectionState
import app.nexscreen.core.network.Hello
import app.nexscreen.core.network.InputMessage
import java.util.concurrent.atomic.AtomicReference

class MainActivity : ComponentActivity() {

    private lateinit var controller: StreamController

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        controller = StreamController(lifecycleScope)
        controller.hello = currentHello()
        setupWindow()

        val surfaceCallback = object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) = controller.setSurface(holder.surface)

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

            override fun surfaceDestroyed(holder: SurfaceHolder) = controller.setSurface(null)
        }

        setContent {
            val state by controller.ui.collectAsState()
            StreamScreen(state, surfaceCallback, controller::sendInput)
        }
    }

    override fun onStart() {
        super.onStart()
        controller.start()
    }

    override fun onStop() {
        controller.stop()
        super.onStop()
    }

    override fun onDestroy() {
        controller.release()
        super.onDestroy()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        // The Activity survives rotation; the next HELLO reports the new orientation
        controller.hello = currentHello()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun setupWindow() {
        // targetSdk 35 is edge-to-edge anyway; draw under the bars and the cutout and keep the screen on
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        hideSystemBars()
    }

    private fun hideSystemBars() {
        WindowCompat.getInsetsController(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }

    /** The physical screen in its current orientation (e.g. 1200x1920 in portrait, 1920x1200 in landscape). */
    private fun currentHello(): Hello {
        val width: Int
        val height: Int
        val rotation: Int
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = windowManager.maximumWindowMetrics.bounds
            width = bounds.width()
            height = bounds.height()
            rotation = display?.rotation ?: Surface.ROTATION_0
        } else {
            val metrics = DisplayMetrics()
            @Suppress("DEPRECATION")
            val display = windowManager.defaultDisplay
            @Suppress("DEPRECATION")
            display.getRealMetrics(metrics)
            width = metrics.widthPixels
            height = metrics.heightPixels
            @Suppress("DEPRECATION")
            rotation = display.rotation
        }
        val degrees = when (rotation) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
        return Hello(
            screenWidth = width.coerceIn(0, 0xFFFF),
            screenHeight = height.coerceIn(0, 0xFFFF),
            densityDpi = resources.configuration.densityDpi.coerceIn(0, 0xFFFF),
            rotation = degrees,
        )
    }
}

/**
 * Full-screen video with touch forwarding (gestures in core/input GestureInterpreter): tap = left click,
 * drag = left drag, long-press = right click, two fingers = scroll, three-finger tap = show/hide the stats panel.
 */
@OptIn(ExperimentalComposeUiApi::class)
@Composable
private fun StreamScreen(state: StreamUiState, surfaceCallback: SurfaceHolder.Callback, sendInput: (InputMessage) -> Unit) {
    var hudVisible by rememberSaveable { mutableStateOf(true) }
    // Where the SurfaceView (the video without the letterbox bars) sits inside the full-screen Box, in Box pixels
    val videoBounds = remember { AtomicReference<VideoRect?>(null) }
    val rendering by rememberUpdatedState(state.rendering)
    val touch = remember {
        TouchInputAdapter(
            videoRect = { if (rendering) videoBounds.get() else null },
            send = sendInput,
            onThreeFingerTap = { hudVisible = !hudVisible },
        )
    }
    DisposableEffect(touch) { onDispose { touch.cancel() } }
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
            .pointerInteropFilter { touch.onTouchEvent(it) },
        contentAlignment = Alignment.Center,
    ) {
        // Letterbox: the largest box with the stream's aspect ratio that fits the screen in either orientation
        val videoModifier = if (state.videoWidth > 0 && state.videoHeight > 0) {
            Modifier.aspectRatio(state.videoWidth.toFloat() / state.videoHeight)
        } else {
            Modifier.fillMaxSize()
        }
        AndroidView(
            factory = { context -> SurfaceView(context).apply { holder.addCallback(surfaceCallback) } },
            modifier = videoModifier.onGloballyPositioned { coordinates ->
                val b = coordinates.boundsInParent()
                videoBounds.set(VideoRect(b.left, b.top, b.width, b.height))
            },
        )

        if (!state.rendering) {
            StatusOverlay(state)
        } else if (hudVisible) {
            Hud(state, Modifier.align(Alignment.TopEnd))
        }
    }
}

@Composable
private fun StatusOverlay(state: StreamUiState) {
    val detail = when (val c = state.connection) {
        ConnectionState.Idle -> stringResource(R.string.status_idle)
        is ConnectionState.Connecting -> stringResource(R.string.status_connecting, c.attempt)
        ConnectionState.Handshaking -> stringResource(R.string.status_handshaking)
        is ConnectionState.Waiting -> stringResource(R.string.status_waiting_retry, c.retryInMs / 1000f)
        is ConnectionState.Streaming -> if (state.decoderUnavailable) {
            stringResource(R.string.status_decoder_unavailable, c.videoWidth, c.videoHeight)
        } else {
            stringResource(R.string.status_streaming_starting, c.videoWidth, c.videoHeight, c.fps)
        }
    }
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFF0B1221))
            .padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        BasicText(
            text = stringResource(R.string.status_waiting_title),
            style = TextStyle(color = Color.White, fontSize = 28.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center),
        )
        BasicText(
            text = detail,
            modifier = Modifier.padding(top = 16.dp).widthIn(max = 640.dp),
            style = TextStyle(color = Color(0xFF9FB3C8), fontSize = 18.sp, textAlign = TextAlign.Center),
        )
        BasicText(
            text = stringResource(R.string.status_hint),
            modifier = Modifier.padding(top = 32.dp).widthIn(max = 640.dp),
            style = TextStyle(color = Color(0xFF61748A), fontSize = 15.sp, textAlign = TextAlign.Center),
        )
    }
}

@Composable
private fun Hud(state: StreamUiState, modifier: Modifier) {
    Column(
        modifier = modifier
            .padding(12.dp)
            .background(Color(0x99000000), RoundedCornerShape(8.dp))
            .padding(horizontal = 10.dp, vertical = 6.dp),
    ) {
        val style = TextStyle(color = Color.White, fontSize = 12.sp)
        BasicText(
            text = stringResource(R.string.hud_streaming, state.videoWidth, state.videoHeight, state.fps, state.displayedFps),
            style = style,
        )
        BasicText(
            text = stringResource(R.string.hud_latency, state.latencyP50Ms, state.latencyP95Ms, state.framesDropped),
            style = style,
        )
        BasicText(text = stringResource(R.string.hud_hint), style = style.copy(color = Color(0xFFB0BEC5)))
    }
}
