package com.airmic.app

import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * UDP 极速网络传输实现 (Wi-Fi 模式)
 * 采用原生 DatagramSocket，将音频帧以最小化协议开销实时发往电脑
 */
class UdpTransport(private val serverIp: String, private val serverPort: Int = 8091) : IAudioTransport {

    companion object {
        private const val TAG = "AirMic-UDP"
    }

    private var socket: DatagramSocket? = null
    private var address: InetAddress? = null
    @Volatile
    private var connected = false
    private var listener: TransportListener? = null

    override fun start() {
        Thread({
            try {
                address = InetAddress.getByName(serverIp)
                socket = DatagramSocket()
                connected = true
                Log.i(TAG, "UDP 传输已就绪 -> $serverIp:$serverPort")
                listener?.onConnected("Wi-Fi UDP: $serverIp:$serverPort")
            } catch (e: Exception) {
                Log.e(TAG, "UDP 初始化失败", e)
                listener?.onError("Wi-Fi 地址无效: ${e.message}")
            }
        }, "AirMic-UDPInit").start()
    }

    override fun stop() {
        connected = false
        socket?.close()
        socket = null
        listener?.onDisconnected("UDP 已断开")
    }

    override fun isConnected(): Boolean = connected && socket != null && !socket!!.isClosed

    override fun sendAudioFrame(data: ByteArray, offset: Int, length: Int) {
        if (!connected || socket == null || address == null) return
        try {
            val packet = DatagramPacket(data, offset, length, address, serverPort)
            socket?.send(packet)
        } catch (e: Exception) {
            Log.e(TAG, "UDP 发送错误", e)
        }
    }

    override fun setListener(listener: TransportListener?) {
        this.listener = listener
    }
}
