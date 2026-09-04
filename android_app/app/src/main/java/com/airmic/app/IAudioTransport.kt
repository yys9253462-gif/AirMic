package com.airmic.app

import java.io.OutputStream

/**
 * 传输通道通用接口，统一 Bluetooth、Wi-Fi (UDP/TCP) 与 USB 数据管道
 */
interface IAudioTransport {
    fun start()
    fun stop()
    fun isConnected(): Boolean
    fun sendAudioFrame(data: ByteArray, offset: Int, length: Int)
    fun setListener(listener: TransportListener?)
}

interface TransportListener {
    fun onConnected(info: String)
    fun onDisconnected(reason: String)
    fun onError(error: String)
}
