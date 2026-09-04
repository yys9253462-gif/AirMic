package com.airmic.app

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.util.Log
import java.io.IOException
import java.io.OutputStream
import java.util.UUID

/**
 * 蓝牙 RFCOMM 传输实现类 (对标 WO Mic 蓝牙传输模式)
 * 手机端作为 Bluetooth Server 侦听指定 SPP UUID，等待 PC 发起连接
 */
class BluetoothTransport : IAudioTransport {

    companion object {
        private const val TAG = "AirMic-Bluetooth"
        private const val SERVICE_NAME = "AirMicVoiceService"
        // 采用标准串口 SPP UUID 或自定义 AirMic UUID
        val AIRMIC_BT_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }

    private val bluetoothAdapter: BluetoothAdapter? = BluetoothAdapter.getDefaultAdapter()
    private var serverSocket: BluetoothServerSocket? = null
    private var connectedSocket: BluetoothSocket? = null
    private var outputStream: OutputStream? = null

    @Volatile
    private var isRunning = false
    private var listener: TransportListener? = null
    private var acceptThread: Thread? = null

    override fun start() {
        if (bluetoothAdapter == null || !bluetoothAdapter.isEnabled) {
            listener?.onError("蓝牙未开启或设备不支持蓝牙")
            return
        }

        isRunning = true
        acceptThread = Thread({
            try {
                // 监听传入的 RFCOMM 通道
                serverSocket = bluetoothAdapter.listenUsingRfcommWithServiceRecord(SERVICE_NAME, AIRMIC_BT_UUID)
                Log.d(TAG, "蓝牙服务端已就绪，正在等待电脑连接...")

                while (isRunning) {
                    val socket = serverSocket?.accept() ?: break
                    synchronized(this) {
                        connectedSocket?.close()
                        connectedSocket = socket
                        outputStream = socket.outputStream
                        val deviceName = try { socket.remoteDevice.name ?: socket.remoteDevice.address } catch (e: SecurityException) { "电脑设备" }
                        Log.i(TAG, "已连接到电脑蓝牙: $deviceName")
                        listener?.onConnected("蓝牙设备: $deviceName")
                    }
                    // 保持单连接会话
                    break
                }
            } catch (e: Exception) {
                if (isRunning) {
                    Log.e(TAG, "蓝牙侦听异常", e)
                    listener?.onError("蓝牙连接异常: ${e.message}")
                }
            }
        }, "AirMic-BTAcceptThread").apply { start() }
    }

    override fun stop() {
        isRunning = false
        try {
            outputStream?.close()
            connectedSocket?.close()
            serverSocket?.close()
        } catch (e: IOException) {
            Log.w(TAG, "关闭蓝牙流异常", e)
        }
        outputStream = null
        connectedSocket = null
        serverSocket = null
        listener?.onDisconnected("蓝牙已停止")
    }

    override fun isConnected(): Boolean {
        return isRunning && connectedSocket != null && connectedSocket!!.isConnected
    }

    override fun sendAudioFrame(data: ByteArray, offset: Int, length: Int) {
        if (!isConnected()) return
        try {
            outputStream?.let { stream ->
                synchronized(stream) {
                    // RFCOMM 是字节流协议，不保留消息边界；先发送 4 字节大端帧长，Windows 端据此完整拆包。
                    val sizePrefix = byteArrayOf(
                        ((length ushr 24) and 0xFF).toByte(),
                        ((length ushr 16) and 0xFF).toByte(),
                        ((length ushr 8) and 0xFF).toByte(),
                        (length and 0xFF).toByte()
                    )
                    stream.write(sizePrefix)
                    stream.write(data, offset, length)
                    stream.flush()
                }
            }
        } catch (e: IOException) {
            Log.e(TAG, "蓝牙发送失败，可能断开", e)
            stop()
            listener?.onDisconnected("蓝牙连接中断")
        }
    }

    override fun setListener(listener: TransportListener?) {
        this.listener = listener
    }
}
