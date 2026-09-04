const fs = require('fs');
const http = require('http');
const path = require('path');
const dgram = require('dgram');
const os = require('os');
const { WebSocketServer, WebSocket } = require('ws');
const bluetoothManager = require('./bluetooth_manager');

// 支持 Node 单文件可执行应用 (SEA) 内嵌资源读取
let seaMod = null;
try { seaMod = require('node:sea'); } catch (e) { seaMod = null; }
const IS_SEA = !!(seaMod && typeof seaMod.isSea === 'function' && seaMod.isSea());

function getAssetBytes(relPath) {
  if (IS_SEA) {
    try { return seaMod.getAsset(relPath); } catch (e) { return null; }
  }
  return null;
}

const HTTP_PORT = 8090;
const UDP_PORT = 8091;

// 获取本机所有局域网 IPv4 地址
function getLocalIpAddresses() {
  const interfaces = os.networkInterfaces();
  const addresses = [];
  for (const k in interfaces) {
    for (const k2 in interfaces[k]) {
      const address = interfaces[k][k2];
      if (address.family === 'IPv4' && !address.internal) {
        addresses.push({ name: k, address: address.address });
      }
    }
  }
  return addresses;
}

// 状态中心
const state = {
  activeClient: null,
  clientInfo: {
    ip: null,
    sampleRate: 44100,
    channels: 1,
    format: 'pcm_s16le',
    bufferSize: 1024
  },
  stats: {
    connected: false,
    packetsReceived: 0,
    bytesReceived: 0,
    lastPacketTime: 0,
    jitterMs: 0,
    packetLoss: 0,
    levelDb: -60,
    isMuted: false,
    gain: 1.0,
    virtualAudioCableDetected: false
  },
  audioRingBuffer: [],
  listeners: new Set()
};

// 检查 Windows 是否安装了虚拟音频设备 (VB-Audio Virtual Cable)
function checkVirtualAudioCable() {
  try {
    const { execSync } = require('child_process');
    const stdout = execSync('powershell -NoProfile -Command "Get-CimInstance Win32_SoundDevice | Select-Object -ExpandProperty Name"', { encoding: 'utf8' });
    if (stdout.includes('CABLE') || stdout.includes('VB-Audio') || stdout.includes('Virtual')) {
      state.stats.virtualAudioCableDetected = true;
      return true;
    }
  } catch (e) {
    // 降级处理
  }
  state.stats.virtualAudioCableDetected = false;
  return false;
}
checkVirtualAudioCable();

// 1. 创建 HTTP 静态托管与 API 服务
const server = http.createServer((req, res) => {
  // CORS 允许
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') {
    res.writeHead(200);
    res.end();
    return;
  }

  const url = new URL(req.url, `http://${req.headers.host}`);

  // API 路由
  if (url.pathname === '/api/status') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({
      ips: getLocalIpAddresses(),
      stats: state.stats,
      clientInfo: state.clientInfo
    }));
    return;
  }

  if (url.pathname === '/api/bluetooth-devices') {
    bluetoothManager.getPairedDevices().then(devices => {
      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ devices }));
    });
    return;
  }

  if (url.pathname === '/api/config') {
    if (req.method === 'POST') {
      let body = '';
      req.on('data', chunk => body += chunk);
      req.on('end', () => {
        try {
          const cfg = JSON.parse(body);
          if (cfg.gain !== undefined) state.stats.gain = cfg.gain;
          if (cfg.isMuted !== undefined) state.stats.isMuted = cfg.isMuted;
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ success: true, stats: state.stats }));
        } catch (err) {
          res.writeHead(400);
          res.end('Bad Request');
        }
      });
      return;
    }
  }

  // 手机端资源路径路由
  if (url.pathname.startsWith('/mobile')) {
    let filePath = url.pathname.replace('/mobile', '');
    if (filePath === '' || filePath === '/') filePath = '/index.html';
    serveStatic('mobile' + filePath, res);
    return;
  }

  // 电脑端控制台 UI 静态托管
  let filePath = url.pathname;
  if (filePath === '' || filePath === '/') filePath = '/index.html';
  serveStatic('desktop/public' + filePath, res);
});

function serveStatic(relPath, res) {
  const cleanRel = relPath.replace(/\\/g, '/').replace(/^\/+/, '');

  // SEA 单文件模式：优先从内嵌资源读取
  const embedded = getAssetBytes(cleanRel);
  if (embedded) {
    const ext = path.extname(cleanRel);
    let contentType = 'text/html; charset=utf-8';
    if (ext === '.js') contentType = 'application/javascript; charset=utf-8';
    else if (ext === '.css') contentType = 'text/css; charset=utf-8';
    else if (ext === '.json') contentType = 'application/json; charset=utf-8';
    else if (ext === '.svg') contentType = 'image/svg+xml';
    else if (ext === '.png') contentType = 'image/png';
    res.writeHead(200, { 'Content-Type': contentType });
    res.end(embedded);
    return;
  }

  // 常规文件模式：基于项目根解析
  const absPath = path.join(__dirname, '..', cleanRel);

  fs.stat(absPath, (err, stats) => {
    if (err || !stats.isFile()) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('404 Not Found');
      return;
    }
    const ext = path.extname(absPath);
    let contentType = 'text/html; charset=utf-8';
    if (ext === '.js') contentType = 'application/javascript; charset=utf-8';
    else if (ext === '.css') contentType = 'text/css; charset=utf-8';
    else if (ext === '.json') contentType = 'application/json; charset=utf-8';
    else if (ext === '.svg') contentType = 'image/svg+xml';
    else if (ext === '.png') contentType = 'image/png';

    res.writeHead(200, { 'Content-Type': contentType });
    fs.createReadStream(absPath).pipe(res);
  });
}

// 2. 创建 WebSocket 服务
const wss = new WebSocketServer({ noServer: true });

server.on('upgrade', (request, socket, head) => {
  const pathname = new URL(request.url, `http://${request.headers.host}`).pathname;

  if (pathname === '/ws-audio') {
    wss.handleUpgrade(request, socket, head, (ws) => {
      wss.emit('connection', ws, request, 'phone-client');
    });
  } else if (pathname === '/ws-ui') {
    wss.handleUpgrade(request, socket, head, (ws) => {
      wss.emit('connection', ws, request, 'ui-client');
    });
  } else {
    socket.destroy();
  }
});

wss.on('connection', (ws, req, clientType) => {
  const clientIp = req.socket.remoteAddress;

  if (clientType === 'phone-client') {
    console.log(`[+] 手机端已连接: ${clientIp}`);
    state.activeClient = ws;
    state.stats.connected = true;
    state.clientInfo.ip = clientIp;
    broadcastUiState();

    ws.on('message', (data, isBinary) => {
      if (isBinary) {
        processIncomingAudioBuffer(data);
      } else {
        try {
          const msg = JSON.parse(data.toString());
          if (msg.type === 'handshake') {
            state.clientInfo.sampleRate = msg.sampleRate || 44100;
            state.clientInfo.channels = msg.channels || 1;
            state.clientInfo.bufferSize = msg.bufferSize || 1024;
            broadcastUiState();
          } else if (msg.type === 'ping') {
            ws.send(JSON.stringify({ type: 'pong', t: msg.t }));
          }
        } catch (e) {}
      }
    });

    ws.on('close', () => {
      console.log(`[-] 手机端断开: ${clientIp}`);
      if (state.activeClient === ws) {
        state.activeClient = null;
        state.stats.connected = false;
        state.stats.levelDb = -60;
        broadcastUiState();
      }
    });

    ws.on('error', (err) => {
      console.error(`[!] 手机连接异常:`, err.message);
    });

  } else if (clientType === 'ui-client') {
    // 电脑端前端界面控制信令
    state.listeners.add(ws);
    ws.send(JSON.stringify({
      type: 'init',
      ips: getLocalIpAddresses(),
      stats: state.stats,
      clientInfo: state.clientInfo
    }));

    ws.on('message', (data) => {
      try {
        const msg = JSON.parse(data.toString());
        if (msg.type === 'control') {
          if (msg.action === 'mute') state.stats.isMuted = true;
          if (msg.action === 'unmute') state.stats.isMuted = false;
          if (msg.gain !== undefined) state.stats.gain = msg.gain;
          broadcastUiState();

          // 遥控手机端
          if (state.activeClient && state.activeClient.readyState === WebSocket.OPEN) {
            state.activeClient.send(JSON.stringify({
              type: 'remote_control',
              action: state.stats.isMuted ? 'mute' : 'unmute'
            }));
          }
        }
      } catch (err) {}
    });

    ws.on('close', () => {
      state.listeners.delete(ws);
    });
  }
});

// 处理音频二进制缓冲帧并计算电平
let lastProcessedTime = Date.now();
function processIncomingAudioBuffer(buffer) {
  state.stats.packetsReceived++;
  state.stats.bytesReceived += buffer.length;
  const now = Date.now();
  state.stats.jitterMs = Math.abs(now - lastProcessedTime - 23); // 预估抖动
  lastProcessedTime = now;

  // 检验帧头 Magic 'AM'
  if (buffer.length < 8) return;
  const magic = buffer.toString('utf8', 0, 2);
  if (magic !== 'AM') return;

  const seq = buffer.readUInt16LE(2);
  const timestamp = buffer.readUInt32LE(4);

  // PCM 16-bit 采样数据
  const pcmOffset = 8;
  const sampleCount = (buffer.length - pcmOffset) / 2;

  let sum = 0;
  for (let i = 0; i < sampleCount; i++) {
    const sampleVal = buffer.readInt16LE(pcmOffset + i * 2);
    sum += sampleVal * sampleVal;
  }

  const rms = Math.sqrt(sum / (sampleCount || 1));
  const maxPossible = 32768;
  const db = 20 * Math.log10(Math.max(rms / maxPossible, 0.0001));
  state.stats.levelDb = Math.round(db);

  // 广播到 UI 监听器 (包含简要波形预览)
  if (state.listeners.size > 0 && state.stats.packetsReceived % 2 === 0) {
    const uiPayload = JSON.stringify({
      type: 'audio_frame',
      levelDb: state.stats.isMuted ? -60 : state.stats.levelDb,
      packets: state.stats.packetsReceived,
      bytes: state.stats.bytesReceived,
      jitter: state.stats.jitterMs
    });
    for (const listener of state.listeners) {
      if (listener.readyState === WebSocket.OPEN) {
        listener.send(uiPayload);
      }
    }
  }
}

// 状态广播
function broadcastUiState() {
  const payload = JSON.stringify({
    type: 'state_update',
    stats: state.stats,
    clientInfo: state.clientInfo
  });
  for (const listener of state.listeners) {
    if (listener.readyState === WebSocket.OPEN) {
      listener.send(payload);
    }
  }
}

// 3. UDP 高速低延迟接收器 (端口 8091)
const udpServer = dgram.createSocket('udp4');
udpServer.on('message', (msg, rinfo) => {
  processIncomingAudioBuffer(msg);
});
udpServer.on('error', (err) => {
  console.error('[UDP Error]:', err.message);
});
udpServer.bind(UDP_PORT, () => {
  console.log(`[+] UDP 极速音频接收通道就绪: 0.0.0.0:${UDP_PORT}`);
});

// 启动主 HTTP/WS 监听
server.listen(HTTP_PORT, '0.0.0.0', () => {
  const ips = getLocalIpAddresses();
  console.log(`===================================================`);
  console.log(`  AirMic 电脑端服务器已启动 (端口: ${HTTP_PORT})`);
  console.log(`  电脑端控制面板: http://localhost:${HTTP_PORT}`);
  console.log(`  手机端直接访问:`);
  ips.forEach(item => {
    console.log(`    - [${item.name}] http://${item.address}:${HTTP_PORT}/mobile`);
  });
  console.log(`===================================================`);
});
