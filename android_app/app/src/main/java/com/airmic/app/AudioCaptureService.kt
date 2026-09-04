package com.airmic.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 原生后台音频采集服务 (AudioCaptureService)
 * 具备以下关键特性：
 * 1. 采用 AudioRecord 直接采集未压缩 PCM 流，无编解码延迟。
 * 2. 具备前台常驻服务与 WakeLock，防止手机锁屏或切换后台时音频断流。
 * 3. 动态包装 AirMic 8 字节二进制头 (Magic 0x41 0x4D + Seq + Timestamp)。
 */
class AudioCaptureService : Service(), TransportListener {

    companion object {
        private const val TAG = "AirMic-Service"
        private const val CHANNEL_ID = "airmic_audio_channel"
        private const val NOTIFICATION_ID = 1001

        const val ACTION_START = "com.airmic.action.START"
        const val ACTION_STOP = "com.airmic.action.STOP"
        const val EXTRA_TRANSPORT_TYPE = "extra_transport_type"
        const val EXTRA_SERVER_IP = "extra_server_ip"
        const val EXTRA_SAMPLE_RATE = "extra_sample_rate"
    }

    private val binder = LocalBinder()
    inner class LocalBinder : Binder() {
        fun getService(): AudioCaptureService = this@AudioCaptureService
    }

    private var audioRecord: AudioRecord? = null
    private var recordThread: Thread? = null
    @Volatile
    private var isRecording = false

    // 硬件级音频降噪与回声消除控制器
    private var noiseSuppressor: NoiseSuppressor? = null
    private var echoCanceler: AcousticEchoCanceler? = null
    private var autoGainControl: AutomaticGainControl? = null

    private var transport: IAudioTransport? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var seqNum: Short = 0

    var sampleRate: Int = 44100
    var transportType: String = "bluetooth" // "bluetooth", "wifi", "usb"
    var serverIp: String = "192.168.1.100"

    interface ServiceCallback {
        fun onStatusChanged(status: String)
        fun onLevelUpdate(db: Int)
    }
    var callback: ServiceCallback? = null

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AirMic::RecordingLock")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                transportType = intent.getStringExtra(EXTRA_TRANSPORT_TYPE) ?: "bluetooth"
                serverIp = intent.getStringExtra(EXTRA_SERVER_IP) ?: "192.168.1.100"
                sampleRate = intent.getIntExtra(EXTRA_SAMPLE_RATE, 44100)
                startCapture()
            }
            ACTION_STOP -> {
                stopCapture()
            }
        }
        return START_NOT_STICKY
    }

    fun startCapture() {
        if (isRecording) return
        startForeground(NOTIFICATION_ID, buildNotification("正在运行", "手机麦克风已就绪"))
        wakeLock?.acquire(10 * 60 * 1000L) // 保持 CPU 活跃

        // 根据选择的传输媒介初始化
        transport = when (transportType.lowercase()) {
            "bluetooth" -> BluetoothTransport()
            "wifi" -> UdpTransport(serverIp, 8091)
            "usb" -> UdpTransport("127.0.0.1", 8091) // USB ADB 映射本地端口
            else -> BluetoothTransport()
        }
        transport?.setListener(this)
        transport?.start()

        isRecording = true
        startAudioRecordThread()
    }

    private fun startAudioRecordThread() {
        val channelConfig = AudioFormat.CHANNEL_IN_MONO
        val audioFormat = AudioFormat.ENCODING_PCM_16BIT
        val minBufferSize = AudioRecord.getMinBufferSize(sampleRate, channelConfig, audioFormat)
        // 降低内部采样缓冲区，既保证安全又不堆积过多音频帧
        val bufferSize = Math.max(minBufferSize, 512 * 2)

        try {
            // 使用 VOICE_COMMUNICATION 音频源：触发系统底层降噪、回声抑制以及硬件语音增益
            audioRecord = AudioRecord(
                MediaRecorder.AudioSource.VOICE_COMMUNICATION,
                sampleRate,
                channelConfig,
                audioFormat,
                bufferSize
            )

            // 如果特定机型不支持 VOICE_COMMUNICATION，平滑回退到 MIC
            if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
                audioRecord = AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    sampleRate,
                    channelConfig,
                    audioFormat,
                    bufferSize
                )
            }

            if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
                Log.e(TAG, "AudioRecord 初始化失败")
                callback?.onStatusChanged("麦克风初始化失败")
                return
            }

            // 启用 Android 硬件级噪音消除 (NoiseSuppressor)
            val audioSessionId = audioRecord?.audioSessionId ?: 0
            if (audioSessionId != 0) {
                try {
                    if (NoiseSuppressor.isAvailable()) {
                        noiseSuppressor = NoiseSuppressor.create(audioSessionId).apply {
                            enabled = true
                        }
                        Log.i(TAG, "硬件噪音抑制器 (NoiseSuppressor) 启动成功")
                    }
                    if (AcousticEchoCanceler.isAvailable()) {
                        echoCanceler = AcousticEchoCanceler.create(audioSessionId).apply {
                            enabled = true
                        }
                        Log.i(TAG, "回声消除器 (AcousticEchoCanceler) 启动成功")
                    }
                    if (AutomaticGainControl.isAvailable()) {
                        autoGainControl = AutomaticGainControl.create(audioSessionId).apply {
                            enabled = true
                        }
                        Log.i(TAG, "自动增益控制 (AGC) 启动成功")
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "启用硬件音频特效异常", e)
                }
            }

            audioRecord?.startRecording()
        } catch (e: SecurityException) {
            Log.e(TAG, "录音权限拒绝", e)
            callback?.onStatusChanged("缺少录音权限")
            return
        }

        recordThread = Thread({
            // 每次仅采集 256 个采样点（在 48kHz 下仅约 5.3 毫秒延迟，极速出流！）
            val chunkSamples = 256
            val audioData = ShortArray(chunkSamples)
            val headerSize = 8
            val byteBuffer = ByteBuffer.allocate(headerSize + audioData.size * 2).order(ByteOrder.LITTLE_ENDIAN)

            while (isRecording) {
                val readSamples = audioRecord?.read(audioData, 0, audioData.size) ?: 0
                if (readSamples > 0) {
                    // 1. 计算当前 RMS 电平分贝值
                    var sum = 0.0
                    for (i in 0 until readSamples) {
                        sum += audioData[i] * audioData[i]
                    }
                    val rms = Math.sqrt(sum / readSamples)
                    val db = (20 * Math.log10(Math.max(rms / 32768.0, 0.0001))).toInt()
                    callback?.onLevelUpdate(db)

                    // 2. 打包 AirMic 8 字节二进制头
                    byteBuffer.clear()
                    byteBuffer.put(0x41.toByte()) // 'A'
                    byteBuffer.put(0x4D.toByte()) // 'M'
                    byteBuffer.putShort(seqNum++) // 序列号
                    byteBuffer.putInt((System.currentTimeMillis() and 0xFFFFFFFFL).toInt()) // 时间戳

                    // 写入 PCM 采样数据
                    for (i in 0 until readSamples) {
                        byteBuffer.putShort(audioData[i])
                    }

                    // 3. 通过当前传输层实时发送
                    transport?.sendAudioFrame(byteBuffer.array(), 0, headerSize + readSamples * 2)
                }
            }
        }, "AirMic-AudioRecordThread").apply { start() }
    }

    fun stopCapture() {
        isRecording = false
        try {
            noiseSuppressor?.release()
            echoCanceler?.release()
            autoGainControl?.release()
        } catch (e: Exception) { }
        noiseSuppressor = null
        echoCanceler = null
        autoGainControl = null

        try {
            audioRecord?.stop()
            audioRecord?.release()
        } catch (e: Exception) {
            Log.w(TAG, "释放 AudioRecord 异常", e)
        }
        audioRecord = null
        recordThread = null

        transport?.stop()
        transport = null

        if (wakeLock?.isHeld == true) {
            wakeLock?.release()
        }
        stopForeground(true)
        stopSelf()
        callback?.onStatusChanged("已停止录音")
    }

    override fun onConnected(info: String) {
        callback?.onStatusChanged("已连接: $info")
        updateNotification("正在传输音频", info)
    }

    override fun onDisconnected(reason: String) {
        callback?.onStatusChanged("未连接 ($reason)")
        updateNotification("待命", reason)
    }

    override fun onError(error: String) {
        callback?.onStatusChanged("异常: $error")
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "AirMic 麦克风传输服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = "保持后台麦克风音频采集低延迟不间断传输" }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(title: String, content: String): Notification {
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("AirMic 手机无线麦克风 - $title")
            .setContentText(content)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(title: String, content: String) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, buildNotification(title, content))
    }

    override fun onDestroy() {
        stopCapture()
        super.onDestroy()
    }
}
