// SPDX-License-Identifier: LGPL-3.0-or-later
//! Loopback-only TLS 1.3 transport host for the standalone S7 Plus simulator.
//!
//! The PLC model and S7CommPlus codec stay in managed code. This library only replaces
//! Windows Schannel, which cannot host the TLS 1.3 handshake required by the IGW client
//! on older Windows versions. It never authenticates a user and never persists a key.

use std::ffi::c_void;
use std::io::{Cursor, ErrorKind, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::ptr;
use std::slice;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};

const MAX_TPKT_PAYLOAD: usize = u16::MAX as usize - 7;
const IO_TIMEOUT: Duration = Duration::from_millis(200);

#[repr(C)]
#[derive(Clone, Copy)]
pub struct ByteSlice {
    pub ptr: *const u8,
    pub len: usize,
}

pub type RequestHandler = unsafe extern "C" fn(
    context: *mut c_void,
    request: *const u8,
    request_len: usize,
    output: *mut u8,
    output_capacity: usize,
) -> usize;

struct SharedState {
    stop: AtomicBool,
    online: AtomicUsize,
    handler: RequestHandler,
    context: usize,
    last_error: Mutex<String>,
}

pub struct ServerHandle {
    state: Arc<SharedState>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

static START_ERROR: OnceLock<Mutex<String>> = OnceLock::new();

fn start_error() -> &'static Mutex<String> {
    START_ERROR.get_or_init(|| Mutex::new(String::new()))
}

fn set_start_error(message: impl Into<String>) {
    if let Ok(mut slot) = start_error().lock() {
        *slot = message.into();
    }
}

fn set_error(state: &SharedState, message: impl Into<String>) {
    if let Ok(mut slot) = state.last_error.lock() {
        *slot = message.into();
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_plcsim_s7plus_abi_version() -> u32 {
    1
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn igw_plcsim_s7plus_start(
    port: u16,
    certificate: ByteSlice,
    private_key: ByteSlice,
    handler: Option<RequestHandler>,
    context: *mut c_void,
) -> *mut ServerHandle {
    let Some(handler) = handler else {
        set_start_error("missing managed request handler");
        return ptr::null_mut();
    };
    if certificate.ptr.is_null() || certificate.len == 0 || private_key.ptr.is_null() || private_key.len == 0 {
        set_start_error("missing in-memory TLS certificate material");
        return ptr::null_mut();
    }

    let cert = unsafe { slice::from_raw_parts(certificate.ptr, certificate.len) }.to_vec();
    let key = unsafe { slice::from_raw_parts(private_key.ptr, private_key.len) }.to_vec();
    let config = match build_tls_config(cert, key) {
        Ok(config) => Arc::new(config),
        Err(error) => {
            set_start_error(format!("TLS setup failed: {error}"));
            return ptr::null_mut();
        }
    };

    let listener = match TcpListener::bind(("127.0.0.1", port)) {
        Ok(listener) => listener,
        Err(error) => {
            set_start_error(format!("cannot listen on 127.0.0.1:{port}: {error}"));
            return ptr::null_mut();
        }
    };
    if let Err(error) = listener.set_nonblocking(true) {
        set_start_error(format!("cannot configure listener: {error}"));
        return ptr::null_mut();
    }

    let state = Arc::new(SharedState {
        stop: AtomicBool::new(false),
        online: AtomicUsize::new(0),
        handler,
        context: context as usize,
        last_error: Mutex::new(String::new()),
    });
    let worker_state = Arc::clone(&state);
    let worker = thread::spawn(move || accept_loop(listener, config, worker_state));
    let handle = ServerHandle {
        state,
        worker: Mutex::new(Some(worker)),
    };
    Box::into_raw(Box::new(handle))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn igw_plcsim_s7plus_online_count(handle: *const ServerHandle) -> usize {
    if handle.is_null() {
        return 0;
    }
    unsafe { &*handle }.state.online.load(Ordering::Relaxed)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn igw_plcsim_s7plus_last_error(
    handle: *const ServerHandle,
    output: *mut u8,
    capacity: usize,
) -> usize {
    let message = if handle.is_null() {
        start_error().lock().map(|value| value.clone()).unwrap_or_default()
    } else {
        unsafe { &*handle }
            .state
            .last_error
            .lock()
            .map(|value| value.clone())
            .unwrap_or_default()
    };
    if output.is_null() || capacity < message.len() {
        return message.len();
    }
    if !message.is_empty() {
        unsafe { ptr::copy_nonoverlapping(message.as_ptr(), output, message.len()) };
    }
    message.len()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn igw_plcsim_s7plus_stop(handle: *mut ServerHandle) {
    if handle.is_null() {
        return;
    }
    let handle = unsafe { Box::from_raw(handle) };
    handle.state.stop.store(true, Ordering::Release);
    if let Ok(mut slot) = handle.worker.lock() {
        if let Some(worker) = slot.take() {
            let _ = worker.join();
        }
    }
}

fn build_tls_config(certificate: Vec<u8>, private_key: Vec<u8>) -> Result<rustls::ServerConfig, rustls::Error> {
    let base = rustls::crypto::ring::default_provider();
    let provider = rustls::crypto::CryptoProvider {
        cipher_suites: vec![
            rustls::crypto::ring::cipher_suite::TLS13_AES_256_GCM_SHA384,
            rustls::crypto::ring::cipher_suite::TLS13_AES_128_GCM_SHA256,
        ],
        ..base
    };
    rustls::ServerConfig::builder_with_provider(Arc::new(provider))
        .with_protocol_versions(&[&rustls::version::TLS13])?
        .with_no_client_auth()
        .with_single_cert(
            vec![CertificateDer::from(certificate)],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(private_key)),
        )
}

fn accept_loop(listener: TcpListener, config: Arc<rustls::ServerConfig>, state: Arc<SharedState>) {
    let mut clients = Vec::new();
    while !state.stop.load(Ordering::Acquire) {
        match listener.accept() {
            Ok((stream, _)) => {
                let client_state = Arc::clone(&state);
                let client_config = Arc::clone(&config);
                clients.push(thread::spawn(move || handle_client(stream, client_config, client_state)));
            }
            Err(error) if error.kind() == ErrorKind::WouldBlock => thread::sleep(Duration::from_millis(20)),
            Err(error) => {
                set_error(&state, format!("accept failed: {error}"));
                break;
            }
        }
    }
    for client in clients {
        let _ = client.join();
    }
}

fn handle_client(mut stream: TcpStream, config: Arc<rustls::ServerConfig>, state: Arc<SharedState>) {
    state.online.fetch_add(1, Ordering::Relaxed);
    let result = (|| -> Result<(), String> {
        // Accepted sockets can inherit the listener's non-blocking mode on Windows. The
        // protocol pump is deliberately blocking with bounded timeouts.
        stream.set_nonblocking(false).map_err(|e| e.to_string())?;
        stream.set_nodelay(true).map_err(|e| e.to_string())?;
        stream.set_read_timeout(Some(Duration::from_secs(5))).map_err(|e| e.to_string())?;
        stream.set_write_timeout(Some(Duration::from_secs(3))).map_err(|e| e.to_string())?;

        let cr = recv_tpkt_frame(&mut stream, &state)?;
        send_connection_confirm(&mut stream, &cr)?;

        let init_request = recv_iso_packet(&mut stream, &state)?;
        let init_response = invoke_handler(&state, &init_request)?;
        send_iso_packet(&mut stream, &init_response)?;

        let mut tls = rustls::ServerConnection::new(config).map_err(|e| e.to_string())?;
        drive_handshake(&mut stream, &mut tls, &state)?;
        stream.set_read_timeout(Some(IO_TIMEOUT)).map_err(|e| e.to_string())?;

        let mut plaintext = Vec::new();
        while !state.stop.load(Ordering::Acquire) {
            match recv_iso_packet(&mut stream, &state) {
                Ok(packet) => {
                    feed_tls(&mut tls, &packet)?;
                    read_plaintext(&mut tls, &mut plaintext)?;
                    while let Some(request) = take_telegram(&mut plaintext)? {
                        let response = invoke_handler(&state, &request)?;
                        tls.writer().write_all(&response).map_err(|e| e.to_string())?;
                        flush_tls(&mut stream, &mut tls)?;
                    }
                }
                Err(error) if error == "timeout" => continue,
                Err(error) => return Err(error),
            }
        }
        Ok(())
    })();
    if let Err(error) = result {
        if !state.stop.load(Ordering::Acquire) && error != "connection closed" {
            set_error(&state, error);
        }
    }
    state.online.fetch_sub(1, Ordering::Relaxed);
}

fn drive_handshake(
    stream: &mut TcpStream,
    tls: &mut rustls::ServerConnection,
    state: &SharedState,
) -> Result<(), String> {
    while tls.is_handshaking() && !state.stop.load(Ordering::Acquire) {
        flush_tls(stream, tls)?;
        if !tls.is_handshaking() {
            break;
        }
        match recv_iso_packet(stream, state) {
            Ok(packet) => feed_tls(tls, &packet)?,
            Err(error) if error == "timeout" => continue,
            Err(error) => return Err(error),
        }
    }
    flush_tls(stream, tls)
}

fn feed_tls(tls: &mut rustls::ServerConnection, packet: &[u8]) -> Result<(), String> {
    let mut cursor = Cursor::new(packet);
    while cursor.position() < packet.len() as u64 {
        let count = tls.read_tls(&mut cursor).map_err(|e| e.to_string())?;
        if count == 0 {
            break;
        }
        tls.process_new_packets().map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn flush_tls(stream: &mut TcpStream, tls: &mut rustls::ServerConnection) -> Result<(), String> {
    while tls.wants_write() {
        let mut encrypted = Vec::new();
        tls.write_tls(&mut encrypted).map_err(|e| e.to_string())?;
        if encrypted.is_empty() {
            break;
        }
        send_iso_packet(stream, &encrypted)?;
    }
    Ok(())
}

fn read_plaintext(tls: &mut rustls::ServerConnection, output: &mut Vec<u8>) -> Result<(), String> {
    let mut buffer = [0u8; 8192];
    loop {
        match tls.reader().read(&mut buffer) {
            Ok(0) => break,
            Ok(count) => output.extend_from_slice(&buffer[..count]),
            Err(error) if error.kind() == ErrorKind::WouldBlock => break,
            Err(error) => return Err(error.to_string()),
        }
    }
    Ok(())
}

fn invoke_handler(state: &SharedState, request: &[u8]) -> Result<Vec<u8>, String> {
    let context = state.context as *mut c_void;
    // Ordinary S7 Plus responses fit comfortably in this buffer. Large Explore responses
    // report their required size and are repeated; write requests are never repeated because
    // their acknowledgements are tiny.
    let mut response = vec![0u8; 64 * 1024];
    let mut written = unsafe {
        (state.handler)(context, request.as_ptr(), request.len(), response.as_mut_ptr(), response.len())
    };
    if written > response.len() {
        response.resize(written, 0);
        written = unsafe {
            (state.handler)(context, request.as_ptr(), request.len(), response.as_mut_ptr(), response.len())
        };
    }
    if written == 0 || written > response.len() {
        return Err("managed S7 Plus handler returned an invalid response length".to_string());
    }
    response.truncate(written);
    Ok(response)
}

fn take_telegram(buffer: &mut Vec<u8>) -> Result<Option<Vec<u8>>, String> {
    let mut offset = 0usize;
    loop {
        if buffer.len().saturating_sub(offset) < 4 {
            return Ok(None);
        }
        if buffer[offset] != 0x72 {
            return Err("invalid S7 Plus telegram header".to_string());
        }
        let length = u16::from_be_bytes([buffer[offset + 2], buffer[offset + 3]]) as usize;
        let end = offset + 4 + length;
        if buffer.len() < end {
            return Ok(None);
        }
        offset = end;
        if length == 0 {
            return Ok(Some(buffer.drain(..offset).collect()));
        }
    }
}

fn recv_tpkt_frame(stream: &mut TcpStream, state: &SharedState) -> Result<Vec<u8>, String> {
    let mut header = [0u8; 4];
    read_exact_interruptible(stream, &mut header, state)?;
    if header[0] != 0x03 {
        return Err("invalid TPKT version".to_string());
    }
    let total = u16::from_be_bytes([header[2], header[3]]) as usize;
    if total < 4 {
        return Err("invalid TPKT length".to_string());
    }
    let mut frame = vec![0u8; total - 4];
    read_exact_interruptible(stream, &mut frame, state)?;
    Ok(frame)
}

fn recv_iso_packet(stream: &mut TcpStream, state: &SharedState) -> Result<Vec<u8>, String> {
    let mut payload = Vec::new();
    loop {
        let frame = recv_tpkt_frame(stream, state)?;
        if frame.len() < 3 || frame[1] != 0xf0 {
            return Err("expected COTP data frame".to_string());
        }
        let data_start = 1usize + frame[0] as usize;
        if data_start > frame.len() {
            return Err("invalid COTP header length".to_string());
        }
        let eot = frame[2] & 0x80 != 0;
        payload.extend_from_slice(&frame[data_start..]);
        if eot {
            return Ok(payload);
        }
    }
}

fn read_exact_interruptible(stream: &mut TcpStream, buffer: &mut [u8], state: &SharedState) -> Result<(), String> {
    let mut offset = 0usize;
    let started = Instant::now();
    while offset < buffer.len() {
        if state.stop.load(Ordering::Acquire) {
            return Err("connection closed".to_string());
        }
        match stream.read(&mut buffer[offset..]) {
            Ok(0) => return Err("connection closed".to_string()),
            Ok(count) => offset += count,
            Err(error) if matches!(error.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => {
                if offset == 0 {
                    return Err("timeout".to_string());
                }
                if started.elapsed() > Duration::from_secs(3) {
                    return Err("partial TPKT read timed out".to_string());
                }
            }
            Err(error) => return Err(error.to_string()),
        }
    }
    Ok(())
}

fn send_connection_confirm(stream: &mut TcpStream, request: &[u8]) -> Result<(), String> {
    if request.len() < 7 || request[1] != 0xe0 {
        return Err("expected COTP connection request".to_string());
    }
    let response = [
        0x03, 0x00, 0x00, 0x0b,
        0x06, 0xd0, request[4], request[5], 0x00, 0x01, 0x00,
    ];
    stream.write_all(&response).map_err(|e| e.to_string())?;
    stream.flush().map_err(|e| e.to_string())
}

fn send_iso_packet(stream: &mut TcpStream, payload: &[u8]) -> Result<(), String> {
    let mut offset = 0usize;
    loop {
        let remaining = payload.len().saturating_sub(offset);
        let count = remaining.min(MAX_TPKT_PAYLOAD);
        let last = offset + count >= payload.len();
        let total = 7 + count;
        let mut frame = Vec::with_capacity(total);
        frame.extend_from_slice(&[0x03, 0x00]);
        frame.extend_from_slice(&(total as u16).to_be_bytes());
        frame.extend_from_slice(&[0x02, 0xf0, if last { 0x80 } else { 0x00 }]);
        frame.extend_from_slice(&payload[offset..offset + count]);
        stream.write_all(&frame).map_err(|e| e.to_string())?;
        offset += count;
        if last {
            break;
        }
    }
    stream.flush().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extracts_one_complete_chunked_telegram() {
        let mut bytes = vec![0x72, 2, 0, 2, 1, 2, 0x72, 2, 0, 1, 3, 0x72, 2, 0, 0, 99];
        let telegram = take_telegram(&mut bytes).unwrap().unwrap();
        assert_eq!(telegram.len(), 15);
        assert_eq!(bytes, vec![99]);
    }
}
