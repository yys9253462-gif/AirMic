package com.airmic.app

import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer

/** Reliable framed transport used by USB ADB port forwarding. */
class TcpTransport(private val host: String, private val port: Int = 8091) : IAudioTransport {
    @Volatile private var connected = false
    @Volatile private var running = false
    private var socket: Socket? = null
    private var output: BufferedOutputStream? = null
    private var listener: TransportListener? = null

    override fun start() {
        running = true
        Thread {
            while (running && !connected) {
                try {
                    val s = Socket()
                    s.connect(InetSocketAddress(host, port), 1500)
                    socket = s
                    output = BufferedOutputStream(s.getOutputStream(), 64 * 1024)
                    connected = true
                    listener?.onConnected("TCP/USB: $host:$port")
                } catch (e: Exception) {
                    if (running) {
                        listener?.onError("USB TCP 连接失败，正在重试")
                        try { Thread.sleep(1000) } catch (_: InterruptedException) { }
                    }
                }
            }
        }.start()
    }

    override fun stop() {
        running = false
        connected = false
        try { output?.flush() } catch (_: Exception) { }
        try { socket?.close() } catch (_: Exception) { }
        output = null
        socket = null
        listener?.onDisconnected("USB TCP 已断开")
    }

    override fun isConnected() = connected

    override fun sendAudioFrame(data: ByteArray, offset: Int, length: Int) {
        if (!connected) return
        try {
            val prefix = ByteBuffer.allocate(4).putInt(length).array()
            output?.write(prefix)
            output?.write(data, offset, length)
            output?.flush()
        } catch (e: Exception) {
            connected = false
            try { socket?.close() } catch (_: Exception) { }
            socket = null
            output = null
            listener?.onDisconnected("USB TCP 发送失败: ${e.message}")
            if (running) start()
        }
    }

    override fun setListener(listener: TransportListener?) { this.listener = listener }
}
