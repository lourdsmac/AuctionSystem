import { useCallback, useEffect, useRef, useState } from 'react';
import { httpUrl, webSocketUrl } from './apiConfig';

type AuctionState = {
  id: number;
  name: string;
  currentPrice: number;
  lastUpdated: string;
};

type WsMessage =
  | { type: 'connected'; auction: AuctionState }
  | { type: 'auction_update'; payload: AuctionState }
  | { type: 'bid_rejected'; error?: string; auction?: AuctionState };

function formatPrice(n: number) {
  return n.toLocaleString(undefined, { style: 'currency', currency: 'USD' });
}

export default function App() {
  return (
    <>
      <h1>Auction demo: SSE vs WebSocket</h1>
      <p className="sub">
        Left: <strong>Server-Sent Events</strong> — server pushes snapshots every 2s (read-only). Right:{' '}
        <strong>WebSocket</strong> — you send bids; all clients receive live updates.
      </p>
      <div className="grid">
        <SsePanel />
        <WebSocketPanel />
      </div>
      <footer className="note">
        Configure <code>VITE_API_BASE</code> in <code>frontend/.env</code> to point at the API (example:{' '}
        <code>http://localhost:5088</code>) or leave unset to use the Vite proxy for <code>/api</code> and{' '}
        <code>/ws</code>.
      </footer>
    </>
  );
}

function SsePanel() {
  const [status, setStatus] = useState<string>('Disconnected');
  const [statusTone, setStatusTone] = useState<'ok' | 'err' | 'neutral'>('neutral');
  const [auction, setAuction] = useState<AuctionState | null>(null);
  const [log, setLog] = useState<string[]>([]);
  const pushLog = useCallback((line: string) => {
    setLog((prev) => [...prev.slice(-40), `[${new Date().toISOString()}] ${line}`]);
  }, []);

  const esRef = useRef<EventSource | null>(null);
  const reconnectAttempt = useRef(0);
  const stoppedByUser = useRef(false);

  const connect = useCallback(() => {
    stoppedByUser.current = false;
    esRef.current?.close();
    const url = httpUrl('/api/auction/sse');
    pushLog(`Connecting EventSource → ${url}`);

    const es = new EventSource(url);
    esRef.current = es;

    es.onopen = () => {
      reconnectAttempt.current = 0;
      setStatus('Connected');
      setStatusTone('ok');
      pushLog('open');
    };

    es.onmessage = (ev: MessageEvent<string>) => {
      try {
        const data = JSON.parse(ev.data) as AuctionState;
        setAuction(data);
        pushLog(`snapshot price=${data.currentPrice}`);
      } catch {
        pushLog(`parse error: ${ev.data}`);
      }
    };

    es.onerror = () => {
      if (stoppedByUser.current) {
        return;
      }
      setStatusTone('err');
      setStatus('Error / reconnecting…');
      es.close();

      const delay = Math.min(30_000, 1000 * 2 ** reconnectAttempt.current);
      reconnectAttempt.current += 1;
      pushLog(`event source error — retry in ${delay} ms`);
      window.setTimeout(() => connect(), delay);
    };
  }, [pushLog]);

  const disconnect = () => {
    stoppedByUser.current = true;
    esRef.current?.close();
    esRef.current = null;
    setStatus('Disconnected');
    setStatusTone('neutral');
    pushLog('closed by user');
  };

  useEffect(() => () => esRef.current?.close(), []);

  return (
    <section className="panel">
      <h2>
        <span className="badge-sse">SSE</span>
        Live feed (server → client)
      </h2>
      <p className="hint">Uses <code>text/event-stream</code>. You cannot place bids here — observe price changes mirrored from shared server state.</p>
      <div className="row">
        <button type="button" className="primary-sse" onClick={connect}>
          Connect
        </button>
        <button type="button" onClick={disconnect}>
          Disconnect
        </button>
      </div>
      <div className={`status ${statusTone === 'ok' ? 'ok' : statusTone === 'err' ? 'err' : 'neutral'}`}>
        {status}
      </div>
      {auction && (
        <>
          <div className="price">{formatPrice(auction.currentPrice)}</div>
          <div className="meta">{auction.name}</div>
          <div className="meta">Updated {auction.lastUpdated}</div>
        </>
      )}
      <div className="pre">{log.join('\n')}</div>
    </section>
  );
}

function WebSocketPanel() {
  const [status, setStatus] = useState<string>('Disconnected');
  const [statusTone, setStatusTone] = useState<'ok' | 'err' | 'neutral'>('neutral');
  const [auction, setAuction] = useState<AuctionState | null>(null);
  const [bid, setBid] = useState('125');
  const [log, setLog] = useState<string[]>([]);
  const wsRef = useRef<WebSocket | null>(null);

  const pushLog = useCallback((line: string) => {
    setLog((prev) => [...prev.slice(-40), `[${new Date().toISOString()}] ${line}`]);
  }, []);

  const applyMessage = useCallback((raw: string) => {
    let msg: WsMessage;
    try {
      msg = JSON.parse(raw) as WsMessage;
    } catch {
      pushLog(`invalid json: ${raw}`);
      return;
    }

    if (msg.type === 'connected') {
      setAuction(msg.auction);
      pushLog(`connected snapshot price=${msg.auction.currentPrice}`);
      return;
    }
    if (msg.type === 'auction_update') {
      setAuction(msg.payload);
      pushLog(`broadcast price=${msg.payload.currentPrice}`);
      return;
    }
    if (msg.type === 'bid_rejected') {
      setStatusTone('err');
      setStatus(msg.error ?? 'Bid rejected');
      if (msg.auction) setAuction(msg.auction);
      pushLog(`rejected: ${msg.error}`);
    }
  }, [pushLog]);

  const connect = useCallback(() => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      pushLog('already connected');
      return;
    }

    wsRef.current?.close();
    const url = webSocketUrl('/ws/auction');
    pushLog(`Connecting WebSocket → ${url}`);

    const ws = new WebSocket(url);
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus('Connected');
      setStatusTone('ok');
      pushLog('open');
    };

    ws.onmessage = (ev: MessageEvent<string>) => {
      applyMessage(ev.data);
    };

    ws.onerror = () => {
      pushLog('socket error');
    };

    ws.onclose = (ev) => {
      setStatus(`Closed (${ev.code})`);
      setStatusTone(ev.wasClean ? 'neutral' : 'err');
      pushLog(`close code=${ev.code} reason=${ev.reason}`);
    };
  }, [applyMessage, pushLog]);

  const disconnect = () => {
    wsRef.current?.close(1000, 'user');
    wsRef.current = null;
    setStatus('Disconnected');
    setStatusTone('neutral');
    pushLog('closed by user');
  };

  const sendBid = () => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      pushLog('not connected');
      return;
    }

    const n = Number(bid);
    if (!Number.isFinite(n) || n <= 0) {
      pushLog('invalid bid amount');
      return;
    }

    ws.send(JSON.stringify({ bidAmount: n }));
    pushLog(`sent bidAmount=${n}`);
  };

  useEffect(() => () => wsRef.current?.close(), []);

  return (
    <section className="panel">
      <h2>
        <span className="badge-ws">WS</span>
        Interactive (client ↔ server)
      </h2>
      <p className="hint">
        Sends JSON bids <code>{`{ "bidAmount": number }`}</code>. Winning bid updates all connected clients via broadcast.
      </p>
      <div className="row">
        <button type="button" className="primary-ws" onClick={connect}>
          Connect
        </button>
        <button type="button" onClick={disconnect}>
          Disconnect
        </button>
      </div>
      <div className="row">
        <label htmlFor="bid-amt">
          Bid (USD)&nbsp;
          <input
            id="bid-amt"
            type="number"
            min={0}
            step="0.01"
            value={bid}
            onChange={(e) => setBid(e.target.value)}
          />
        </label>
        <button type="button" onClick={sendBid}>
          Send bid
        </button>
      </div>
      <div className={`status ${statusTone === 'ok' ? 'ok' : statusTone === 'err' ? 'err' : 'neutral'}`}>{status}</div>
      {auction && (
        <>
          <div className="price">{formatPrice(auction.currentPrice)}</div>
          <div className="meta">{auction.name}</div>
          <div className="meta">Updated {auction.lastUpdated}</div>
        </>
      )}
      <div className="pre">{log.join('\n')}</div>
    </section>
  );
}
