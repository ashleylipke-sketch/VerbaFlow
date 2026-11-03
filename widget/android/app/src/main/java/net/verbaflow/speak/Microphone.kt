package net.verbaflow.speak

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder

/** The phone's microphone as 16 kHz 16-bit mono sound, in pieces of a tenth of a second, as VerbaFlow expects it. */
class Microphone {
    @Volatile private var record: AudioRecord? = null
    private var thread: Thread? = null

    /** Called on the microphone's own thread with each piece of sound. */
    @Volatile var sound: ((ByteArray) -> Unit)? = null

    val isOn: Boolean get() = record != null

    /** The caller has checked that the microphone is allowed. */
    @SuppressLint("MissingPermission")
    fun start() {
        if (record != null) return
        val rate = 16_000
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val r = AudioRecord(
            MediaRecorder.AudioSource.VOICE_RECOGNITION, rate,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, 3200 * 4)
        )
        if (r.state != AudioRecord.STATE_INITIALIZED) {
            r.release()
            throw IllegalStateException("microphone")
        }
        r.startRecording()
        if (r.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            r.release()
            throw IllegalStateException("microphone")
        }
        record = r
        thread = Thread({
            val buf = ByteArray(3200) // a tenth of a second
            while (record === r) {
                val n = r.read(buf, 0, buf.size)
                if (n > 0) sound?.invoke(buf.copyOf(n)) else if (n < 0) break
            }
        }, "VerbaFlow microphone").also { it.start() }
    }

    fun stop() {
        val r = record ?: return
        record = null
        try { r.stop() } catch (_: IllegalStateException) { }
        thread?.join(500)
        thread = null
        r.release()
    }
}
