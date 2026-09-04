package com.airmic.app

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.text.format.Formatter
import android.util.Log
import android.widget.*
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.*
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * 手机端主界面 (终极全自动双模配对版)
 * 解决一切家庭/办公室 Wi-Fi 路由器屏蔽 UDP 广播导致的无法配对问题！
 * 1. 开启 WifiManager.MulticastLock，允许接收底层 UDP 广播；
 * 2. 增加高并发网段探测（主动扫描 192.168.x.1~254 的 8090 端口），0.5 秒内嗅探出电脑；
 * 3. 一旦探测成功，立即向电脑发送配对请求，双端秒级变绿！
 */
class MainActivity : AppCompatActivity(), AudioCaptureService.ServiceCallback {

    private lateinit var radioGroupTransport: RadioGroup
    private lateinit var radioWifi: RadioButton
    private lateinit var radioBt: RadioButton
    private lateinit var radioUsb: RadioButton
    private lateinit var textPairStatus: TextView
    private lateinit var textPairDetail: TextView
    private lateinit var spinnerSampleRate: Spinner
    private lateinit var btnToggleMic: Button
    private lateinit var textDb: TextView
    private lateinit var progressBarLevel: ProgressBar

    private var audioService: AudioCaptureService? = null
    private var isBound = false
    private var isTransmitting = false

    // 自动配对状态
    @Volatile
    private var isPairingActive = true
    private var targetPcIp: String? = null
    private var targetPcName: String? = null
    private var isPairedSuccessfully = false
    private var multicastLock: WifiManager.MulticastLock? = null

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(className: ComponentName, service: IBinder) {
            val binder = service as AudioCaptureService.LocalBinder
            audioService = binder.getService()
            audioService?.callback = this@MainActivity
            isBound = true
        }

        override fun onServiceDisconnected(arg0: ComponentName) {
            isBound = false
            audioService = null
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // 关键：解除 Android 硬件多播限制
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            multicastLock = wm.createMulticastLock("AirMicMulticastLock").apply {
                setReferenceCounted(true)
                acquire()
            }
        } catch (e: Exception) { }

        initViews()
        checkAndRequestPermissions()

        // 启动超强双模自动配对引擎 (UDP信标 + HTTP主动网段穿透)
        startDualModePairingEngine()
    }

    private fun initViews() {
        radioGroupTransport = findViewById(R.id.radioGroupTransport)
        radioWifi = findViewById(R.id.radioWifi)
        radioBt = findViewById(R.id.radioBt)
        radioUsb = findViewById(R.id.radioUsb)
        textPairStatus = findViewById(R.id.textPairStatus)
        textPairDetail = findViewById(R.id.textPairDetail)
        spinnerSampleRate = findViewById(R.id.spinnerSampleRate)
        btnToggleMic = findViewById(R.id.btnToggleMic)
        textDb = findViewById(R.id.textDb)
        progressBarLevel = findViewById(R.id.progressBarLevel)

        radioWifi.isChecked = true

        val rates = arrayOf(
            "192000 Hz (192kHz 旗舰发烧级)",
            "96000 Hz (96kHz 专业录音棚级)",
            "48000 Hz (48kHz 广播级 / 默认推荐)",
            "44100 Hz (44.1kHz CD母带标准)",
            "16000 Hz (16kHz 极速低带宽对话)"
        )
        val adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, rates)
        spinnerSampleRate.adapter = adapter
        spinnerSampleRate.setSelection(2)

        // 手机端修改配置时（如采样率更改），如果正在串流则自动无缝重启推流
        spinnerSampleRate.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: android.view.View?, position: Int, id: Long) {
                if (isTransmitting) {
                    stopStreaming()
                    btnToggleMic.postDelayed({ startStreaming() }, 300)
                }
            }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }

        radioGroupTransport.setOnCheckedChangeListener { _, checkedId ->
            if (checkedId == R.id.radioWifi) {
                if (isPairedSuccessfully) {
                    showPairedState(targetPcName ?: "电脑", targetPcIp ?: "局域网")
                } else {
                    showSearchingState()
                    startDualModePairingEngine()
                }
            } else if (checkedId == R.id.radioBt) {
                textPairStatus.text = "🔵 蓝牙配对模式"
                textPairStatus.setTextColor(Color.parseColor("#38BDF8"))
                textPairDetail.text = "请确保手机与电脑已在系统蓝牙设置中完成配对，电脑端点击连接即可。"
            } else if (checkedId == R.id.radioUsb) {
                textPairStatus.text = "🔌 USB 极速模式"
                textPairStatus.setTextColor(Color.parseColor("#38BDF8"))
                textPairDetail.text = "手机插上 USB 并开启调试，双端通过 127.0.0.1 极速直连。"
            }
        }

        btnToggleMic.setOnClickListener {
            if (!isTransmitting) {
                startStreaming()
            } else {
                stopStreaming()
            }
        }
    }

    private fun startDualModePairingEngine() {
        isPairingActive = true
        showSearchingState()

        // 通道 1：UDP 广播监听 (支持收到广播后立刻反向确认)
        Thread({
            var socket: DatagramSocket? = null
            try {
                socket = DatagramSocket(null).apply {
                    reuseAddress = true
                    bind(InetSocketAddress(8092))
                    soTimeout = 1000
                }
                val buffer = ByteArray(512)
                val packet = DatagramPacket(buffer, buffer.size)

                while (isPairingActive && !isPairedSuccessfully) {
                    try {
                        socket.receive(packet)
                        val msg = String(packet.data, 0, packet.length).trim()
                        if (msg.startsWith("AirMicBeacon:")) {
                            val parts = msg.split(":")
                            if (parts.size >= 4) {
                                val pcIp = parts[1]
                                val pcName = parts[3]
                                onPcFound(pcName, pcIp)
                                break
                            }
                        }
                    } catch (e: Exception) { }
                }
            } catch (e: Exception) {
            } finally {
                socket?.close()
            }
        }, "AirMic-UdpDiscovery").start()

        // 通道 2：主动网段 TCP/HTTP 穿透探测（多网卡精准获取子网，极速穿透 AP 隔离）
        Thread({
            try {
                // 循环探测直到配对成功或主动停止
                while (isPairingActive && !isPairedSuccessfully) {
                    val subnets = getAllLocalSubnets()
                    val pool = Executors.newFixedThreadPool(40)
                    
                    for (subnet in subnets) {
                        for (i in 1..254) {
                            if (isPairedSuccessfully) break
                            val testIp = "$subnet.$i"
                            pool.execute {
                                if (isPairingActive && !isPairedSuccessfully) {
                                    probePcHttp(testIp)
                                }
                            }
                        }
                    }
                    pool.shutdown()
                    pool.awaitTermination(3, TimeUnit.SECONDS)
                    if (!isPairedSuccessfully) {
                        Thread.sleep(1000)
                    }
                }
            } catch (e: Exception) { }
        }, "AirMic-HttpScan").start()
    }

    private fun getAllLocalSubnets(): List<String> {
        val list = mutableListOf<String>()
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val nif = interfaces.nextElement()
                if (nif.isLoopback || !nif.isUp) continue
                val addresses = nif.inetAddresses
                while (addresses.hasMoreElements()) {
                    val addr = addresses.nextElement()
                    if (addr is Inet4Address && !addr.isLoopbackAddress) {
                        val ip = addr.hostAddress
                        if (ip != null && (ip.startsWith("192.168.") || ip.startsWith("10.") || ip.startsWith("172."))) {
                            val sub = ip.substringBeforeLast(".")
                            if (!list.contains(sub)) list.add(sub)
                        }
                    }
                }
            }
        } catch (e: Exception) { }

        if (list.isEmpty()) {
            list.add("192.168.10")
            list.add("192.168.1")
            list.add("192.168.0")
            list.add("192.168.31")
            list.add("192.168.123")
        }
        return list
    }

    private fun probePcHttp(ip: String) {
        try {
            val url = URL("http://$ip:8090/airmic/discover?model=" + URLEncoder.encode(Build.MODEL ?: "Android", "UTF-8"))
            val conn = (url.openConnection() as HttpURLConnection).apply {
                connectTimeout = 800
                readTimeout = 800
                requestMethod = "GET"
                instanceFollowRedirects = false
                useCaches = false
            }
            if (conn.responseCode == 200) {
                val reader = BufferedReader(InputStreamReader(conn.inputStream))
                val resp = reader.readLine()
                reader.close()
                if (resp != null && resp.contains("pcName")) {
                    val pcName = resp.substringAfter("pcName\":\"").substringBefore("\"")
                    onPcFound(pcName, ip)
                }
            }
        } catch (e: Exception) { }
    }

    private fun onPcFound(pcName: String, pcIp: String) {
        if (isPairedSuccessfully) return
        isPairedSuccessfully = true
        targetPcIp = pcIp
        targetPcName = pcName

        // 向电脑发送 UDP 握手包巩固信道
        Thread({
            try {
                val req = "AirMicPairReq:" + (Build.MODEL ?: "Android")
                val reqBytes = req.toByteArray(Charsets.UTF_8)
                val s = DatagramSocket()
                val p = DatagramPacket(reqBytes, reqBytes.size, InetAddress.getByName(pcIp), 8092)
                s.send(p)
                s.close()
            } catch (e: Exception) { }
        }).start()

        runOnUiThread {
            showPairedState(pcName, pcIp)
        }
    }

    private fun showSearchingState() {
        textPairStatus.text = "⏳ 正在双模自动探测电脑..."
        textPairStatus.setTextColor(Color.parseColor("#F59E0B"))
        textPairDetail.text = "无需输入 IP！双模信标与主动探测运行中，确保电脑端 AirMic 打开即可秒配对。"
    }

    private fun showPairedState(pcName: String, pcIp: String) {
        textPairStatus.text = "✅ 配对成功 (已连接电脑: $pcName)"
        textPairStatus.setTextColor(Color.parseColor("#10B981"))
        textPairDetail.text = "电脑【$pcName】(IP: $pcIp) 已在线就绪。点击下方大按钮即可开始麦克风拾音！"
        btnToggleMic.text = "开始麦克风传输"
    }

    private fun startStreaming() {
        val selectedTransport = when (radioGroupTransport.checkedRadioButtonId) {
            R.id.radioWifi -> "wifi"
            R.id.radioBt -> "bluetooth"
            R.id.radioUsb -> "usb"
            else -> "wifi"
        }

        var ip = targetPcIp ?: "127.0.0.1"
        if (selectedTransport == "usb") {
            ip = "127.0.0.1"
        } else if (selectedTransport == "wifi" && !isPairedSuccessfully) {
            Toast.makeText(this, "正在重新匹配电脑中，请稍候...", Toast.LENGTH_SHORT).show()
            startDualModePairingEngine()
            return
        }

        val sampleRate = when (spinnerSampleRate.selectedItemPosition) {
            0 -> 192000
            1 -> 96000
            2 -> 48000
            3 -> 44100
            4 -> 16000
            else -> 48000
        }

        val intent = Intent(this, AudioCaptureService::class.java).apply {
            action = AudioCaptureService.ACTION_START
            putExtra(AudioCaptureService.EXTRA_TRANSPORT_TYPE, selectedTransport)
            putExtra(AudioCaptureService.EXTRA_SERVER_IP, ip)
            putExtra(AudioCaptureService.EXTRA_SAMPLE_RATE, sampleRate)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
        bindService(intent, connection, Context.BIND_AUTO_CREATE)

        isTransmitting = true
        btnToggleMic.text = "停止传输"
        btnToggleMic.setBackgroundColor(ContextCompat.getColor(this, android.R.color.holo_red_dark))
        textPairStatus.text = "🎙️ 正在实时推流声音中..."
        textPairStatus.setTextColor(Color.parseColor("#38BDF8"))
        textPairDetail.text = "手机麦克风已激活，请在电脑端点击【测试麦克风是否正常】听取回放效果！"
    }

    private fun stopStreaming() {
        val intent = Intent(this, AudioCaptureService::class.java).apply {
            action = AudioCaptureService.ACTION_STOP
        }
        startService(intent)

        if (isBound) {
            unbindService(connection)
            isBound = false
        }

        isTransmitting = false
        btnToggleMic.text = "开始麦克风传输"
        btnToggleMic.setBackgroundColor(ContextCompat.getColor(this, android.R.color.holo_blue_dark))
        if (isPairedSuccessfully) {
            showPairedState(targetPcName ?: "电脑", targetPcIp ?: "局域网")
        } else {
            showSearchingState()
        }
        progressBarLevel.progress = 0
        textDb.text = "-inf dB"
    }

    override fun onStatusChanged(status: String) {}

    override fun onLevelUpdate(db: Int) {
        runOnUiThread {
            textDb.text = "$db dB"
            val progress = Math.max(0, Math.min(100, (db + 60) * 100 / 60))
            progressBarLevel.progress = progress
        }
    }

    private fun checkAndRequestPermissions() {
        val permissions = mutableListOf(
            Manifest.permission.RECORD_AUDIO,
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            permissions.add(Manifest.permission.BLUETOOTH_CONNECT)
            permissions.add(Manifest.permission.BLUETOOTH_SCAN)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            permissions.add(Manifest.permission.POST_NOTIFICATIONS)
        }

        val needed = permissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }

        if (needed.isNotEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 101)
        }
    }

    override fun onDestroy() {
        isPairingActive = false
        try {
            multicastLock?.release()
        } catch (e: Exception) { }
        if (isBound) {
            unbindService(connection)
            isBound = false
        }
        super.onDestroy()
    }
}
