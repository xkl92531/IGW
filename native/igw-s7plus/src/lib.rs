use std::ffi::c_char;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::slice;
use std::str;
use std::collections::HashMap;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

use s7commplus::value::PValue;
use s7commplus::{Connection, Error};

mod reader;
mod resolver;
mod resync;
mod subscribe;

#[repr(i32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IgwResultCode {
    Ok = 0,
    AuthenticationRequired = 10,
    AuthenticationFailed = 11,
    PermissionDenied = 12,
    UnsupportedFirmware = 20,
    Timeout = 30,
    TransportFailure = 31,
    ProtocolFailure = 32,
    // Reserved for positively identified protocol request/response capacity rejection.
    // The pinned library exposes opaque return_value strings, not a verified size code.
    // Do not map Protocol/Framing/VLQ/Crypto errors or local BufferTooSmall to this.
    CapacityFailure = 33,
    // ABI v2：单个符号在 PLC 程序中找不到(解析失败或 PLC 对该项报错)，只影响该项。
    SymbolNotFound = 34,
    // ABI v3：逐项写入时 PLC 拒绝了该项(类型不符、只读、受保护等)。
    ItemRejected = 35,
    // ABI v4：订阅 id 不存在(已删除或连接重建过)。
    SubscriptionNotFound = 36,
    InvalidArgument = 40,
    BufferTooSmall = 41,
    Panic = 99,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct IgwResult {
    pub code: IgwResultCode,
    pub detail: u32,
}

impl IgwResult {
    const fn new(code: IgwResultCode) -> Self { Self { code, detail: 0 } }

    const fn with_detail(code: IgwResultCode, detail: u32) -> Self { Self { code, detail } }
}

pub const fn abi_version() -> u32 { 5 }

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_abi_version() -> u32 { abi_version() }

#[repr(C)]
pub struct IgwConnectRequest {
    pub host_ptr: *const c_char,
    pub host_len: usize,
    pub port: u16,
    pub timeout_ms: u32,
    pub legacy: u8,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct IgwByteSlice {
    pub ptr: *const u8,
    pub len: usize,
}

impl IgwByteSlice {
    pub const fn empty() -> Self { Self { ptr: std::ptr::null(), len: 0 } }

    pub fn from_bytes(bytes: &[u8]) -> Self { Self { ptr: bytes.as_ptr(), len: bytes.len() } }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct IgwMutableByteSlice {
    pub ptr: *mut u8,
    pub len: usize,
}

#[repr(i32)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum IgwValueType {
    #[default]
    Null = 0,
    Bool = 1,
    USInt = 2,
    UInt = 3,
    UDInt = 4,
    ULInt = 5,
    SInt = 6,
    Int = 7,
    DInt = 8,
    LInt = 9,
    Byte = 10,
    Word = 11,
    DWord = 12,
    LWord = 13,
    Real = 14,
    LReal = 15,
    Timestamp = 16,
    Timespan = 17,
    Text = 18,
    Bytes = 19,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct IgwReadItem {
    pub symbol: IgwByteSlice,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct IgwReadResult {
    pub code: IgwResultCode,
    pub value_type: IgwValueType,
    pub signed_value: i64,
    pub unsigned_value: u64,
    pub float_value: f64,
    pub payload_offset: u32,
    pub payload_length: u32,
}

impl Default for IgwReadResult {
    fn default() -> Self {
        Self {
            code: IgwResultCode::Ok,
            value_type: IgwValueType::Null,
            signed_value: 0,
            unsigned_value: 0,
            float_value: 0.0,
            payload_offset: 0,
            payload_length: 0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct IgwWriteItem {
    pub symbol: IgwByteSlice,
    pub value_type: IgwValueType,
    pub signed_value: i64,
    pub unsigned_value: u64,
    pub float_value: f64,
    pub payload: IgwByteSlice,
}

impl Default for IgwWriteItem {
    fn default() -> Self {
        Self {
            symbol: IgwByteSlice::empty(),
            value_type: IgwValueType::Null,
            signed_value: 0,
            unsigned_value: 0,
            float_value: 0.0,
            payload: IgwByteSlice::empty(),
        }
    }
}

impl IgwMutableByteSlice {
    pub fn from_bytes(bytes: &mut [u8]) -> Self { Self { ptr: bytes.as_mut_ptr(), len: bytes.len() } }
}

pub struct IgwS7PlusHandle {
    connection: Mutex<Option<Session>>,
    /// 活动订阅表。订阅建立在连接上：连接重建后 PLC 侧的订阅就没了，这里一并清空，
    /// 由托管侧重新建立(和读取路径的缓存不同，订阅不能跨重连保留)。
    subscriptions: Mutex<HashMap<u32, subscribe::ActiveSubscription>>,
    next_subscription_id: AtomicU32,
    /// 符号解析缓存：跨重连保留，只有换端点或程序变更自愈时才清空。
    symbols: Mutex<SymbolState>,
    /// 程序变更自愈判断：跨重连保留(限频与“重建后仍出错”的记录不能随连接丢失)。
    resync: Mutex<resync::ResyncState>,
}

/// 把本次调用中被 PLC 拒绝的已解析符号交给自愈判断。
/// 一旦判定要重建连接(PLC 程序多半重新下载过)，顺带清空跨重连保留的符号缓存——
/// 那是唯一需要整体丢弃它的时机。
fn note_rejections(handle: &IgwS7PlusHandle, symbols: &mut SymbolState) {
    if symbols.cache.rejected.is_empty() { return; }
    let rejected = std::mem::take(&mut symbols.cache.rejected);
    let Ok(mut state) = handle.resync.lock() else { return; };
    let now = std::time::Instant::now();
    let mut resyncing = false;
    for symbol in &rejected {
        resyncing |= state.record_item_error(symbol, now);
    }
    drop(state);
    if resyncing { symbols.clear(); }
}

/// ABI v3：取出并清除“需要重建连接”的请求(PLC 程序可能已重新下载，缓存的地址与类型已过期)。
/// 返回 1 时调用方应断开并按正常流程重连、重新认证。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_take_resync_request(handle: *mut IgwS7PlusHandle) -> u8 {
    match catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() { return 0; }
        let handle = unsafe { &*handle };
        handle.resync.lock().map(|mut state| u8::from(state.take_request())).unwrap_or(0)
    })) {
        Ok(value) => value,
        Err(_) => 0,
    }
}

/// ABI v4：建立变化订阅。PLC 按 `cycle_time_ms` 周期主动推送变化的值，客户端不再轮询。
///
/// `results` 每项一个码：Ok = 已登记，SymbolNotFound = 这个符号解析不到(只影响该项)。
/// 成功时 `out_subscription_id` 给出订阅句柄，用它 poll 和 delete。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_subscribe(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwReadItem,
    count: usize,
    cycle_time_ms: u16,
    results: *mut IgwResultCode,
    out_subscription_id: *mut u32,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() || out_subscription_id.is_null() || (count > 0 && (items.is_null() || results.is_null())) {
            return IgwResult::new(IgwResultCode::InvalidArgument);
        }
        if count == 0 { return IgwResult::new(IgwResultCode::InvalidArgument); }
        let input = unsafe { slice::from_raw_parts(items, count) };
        let symbols: Vec<Option<String>> = input.iter()
            .map(|item| unsafe { utf8(item.symbol) }.ok().filter(|symbol| !symbol.trim().is_empty()))
            .collect();
        let symbol_refs: Vec<Option<&str>> = symbols.iter().map(Option::as_deref).collect();

        let handle = unsafe { &*handle };
        let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let Some(session) = slot.as_mut() else { return IgwResult::new(IgwResultCode::TransportFailure); };
        let Ok(mut symbols_state) = handle.symbols.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let state = &mut *symbols_state;
        let created = subscribe::create(
            &mut session.connection, &mut state.resolver, &mut state.cache,
            &symbol_refs, cycle_time_ms, std::time::Instant::now());
        let (active, per_item) = match created {
            Ok(value) => value,
            Err(code) => return IgwResult::new(code),
        };
        let id = handle.next_subscription_id.fetch_add(1, Ordering::Relaxed);
        let Ok(mut subscriptions) = handle.subscriptions.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        subscriptions.insert(id, active);
        let item_count = u32::try_from(subscriptions[&id].item_count).unwrap_or(u32::MAX);
        let codes: Vec<IgwResultCode> = per_item.into_iter()
            .map(|item| match item { Ok(()) => IgwResultCode::Ok, Err(code) => code })
            .collect();
        unsafe {
            std::ptr::copy_nonoverlapping(codes.as_ptr(), results, codes.len());
            *out_subscription_id = id;
        }
        // detail 给出实际登记成功的点数，调用方据此判断有多少点被跳过
        IgwResult::with_detail(IgwResultCode::Ok, item_count)
    })) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

/// ABI v4：取下一批通知值。阻塞到 PLC 推来下一个通知或读超时为止。
///
/// PLC 每个订阅周期都会给一个通知(没变化时是空的)，所以一个周期内就会返回，不会长时间占住连接。
/// 读超时返回 Ok 且 `out_count` 为 0——连接仍然可用，调用方下次再来。
/// `slots` 与 `results` 一一对应，给出每个值属于订阅时的第几项。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_poll_notification(
    handle: *mut IgwS7PlusHandle,
    subscription_id: u32,
    capacity: usize,
    slots: *mut u32,
    results: *mut IgwReadResult,
    out_count: *mut usize,
    payload_output: IgwMutableByteSlice,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() || out_count.is_null() || (capacity > 0 && (slots.is_null() || results.is_null())) {
            return IgwResult::new(IgwResultCode::InvalidArgument);
        }
        let handle = unsafe { &*handle };
        let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let Some(session) = slot.as_mut() else { return IgwResult::new(IgwResultCode::TransportFailure); };
        let Ok(mut subscriptions) = handle.subscriptions.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let Some(active) = subscriptions.get_mut(&subscription_id) else {
            return IgwResult::new(IgwResultCode::SubscriptionNotFound);
        };
        let values = match subscribe::poll(&mut session.connection, active) {
            Ok(values) => values,
            Err(code) => return IgwResult::new(code),
        };
        if values.len() > capacity {
            return IgwResult::with_detail(IgwResultCode::BufferTooSmall, u32::try_from(values.len()).unwrap_or(u32::MAX));
        }
        let mut payload = Vec::new();
        let mut encoded = Vec::with_capacity(values.len());
        let mut item_slots = Vec::with_capacity(values.len());
        for (index, value) in values {
            encoded.push(encode_value(value, &mut payload));
            item_slots.push(u32::try_from(index).unwrap_or(u32::MAX));
        }
        if payload_output.len < payload.len() {
            return IgwResult::with_detail(IgwResultCode::BufferTooSmall, u32::try_from(payload.len()).unwrap_or(u32::MAX));
        }
        unsafe {
            if !payload.is_empty() {
                if payload_output.ptr.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
                std::ptr::copy_nonoverlapping(payload.as_ptr(), payload_output.ptr, payload.len());
            }
            if !encoded.is_empty() {
                std::ptr::copy_nonoverlapping(encoded.as_ptr(), results, encoded.len());
                std::ptr::copy_nonoverlapping(item_slots.as_ptr(), slots, item_slots.len());
            }
            *out_count = encoded.len();
        }
        IgwResult::with_detail(IgwResultCode::Ok, u32::try_from(payload.len()).unwrap_or(u32::MAX))
    })) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

/// ABI v4：删除订阅。连接已断时视为已删除(PLC 侧的订阅随连接一起没了)。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_delete_subscription(handle: *mut IgwS7PlusHandle, subscription_id: u32) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
        let handle = unsafe { &*handle };
        let Ok(mut subscriptions) = handle.subscriptions.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let Some(active) = subscriptions.remove(&subscription_id) else {
            return IgwResult::new(IgwResultCode::Ok);   // 已经没了(多半连接重建过)，幂等
        };
        drop(subscriptions);
        let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
        let Some(session) = slot.as_mut() else { return IgwResult::new(IgwResultCode::Ok); };
        match subscribe::delete(&mut session.connection, &active) {
            Ok(()) => IgwResult::new(IgwResultCode::Ok),
            Err(code) => IgwResult::new(code),
        }
    })) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

/// 一次连接会话。符号解析缓存不在这里：它跨重连保留(见 <see cref="SymbolState"/>)。
struct Session {
    connection: Connection,
}

impl Session {
    fn new(connection: Connection) -> Self {
        Self { connection }
    }
}

/// 符号解析状态，跨重连保留。
///
/// 早先它和连接同寿命，一断线就整体丢弃。现场 32 万点的 S7-1500 上这会自锁：
/// 重连后第一批读取要重新取 DB 列表、重新解析符号，这笔开销算在单次读取的超时里，
/// 于是超时 → 判定连接失效 → 断开重连 → 缓存又空 → 更慢，永远回不到稳态。
/// 保留缓存不会读到错数据：PLC 程序真变了的话，失效地址会被 PLC 逐项报错，
/// 那条路径本来就会作废该符号并交给 resync 判断是否整体重建(重建时这里被清空)。
#[derive(Default)]
struct SymbolState {
    /// 缓存归属的端点(host:port)。换了 PLC 就整体丢弃,避免拿上一台的地址去读这一台。
    endpoint: String,
    cache: reader::SymbolCache,
    resolver: resolver::Resolver,
}

impl SymbolState {
    fn clear(&mut self) {
        self.cache = reader::SymbolCache::default();
        self.resolver = resolver::Resolver::default();
    }

    /// 连接到某端点：端点变了就丢弃旧缓存，同一端点重连则原样保留。
    fn bind(&mut self, endpoint: &str) {
        if self.endpoint != endpoint {
            self.clear();
            self.endpoint = endpoint.to_owned();
        }
    }
}

fn classify(error: &Error) -> IgwResultCode {
    if error.is_timeout() { return IgwResultCode::Timeout; }
    if error.is_connection_lost() { return IgwResultCode::TransportFailure; }
    match error {
        Error::Io(_) => IgwResultCode::TransportFailure,
        Error::Tls(_) | Error::Framing(_) | Error::Protocol(_) | Error::Vlq(_) | Error::Crypto(_) | Error::Closed(_) => IgwResultCode::ProtocolFailure,
        _ => IgwResultCode::ProtocolFailure,
    }
}

fn classify_operation(error: &Error) -> IgwResultCode {
    // s7commplus 0.1.0 exposes unknown PLC return_value codes as Protocol(String).
    // There is no verified capacity-code mapping here: keep the original window until
    // a future native implementation can positively return CapacityFailure.
    let message = error.to_string().to_ascii_lowercase();
    if message.contains("access denied")
        || message.contains("authentication required")
        || message.contains("protection level")
    {
        return IgwResultCode::AuthenticationRequired;
    }
    if message.contains("permission denied") || message.contains("not permitted") {
        return IgwResultCode::PermissionDenied;
    }
    classify(error)
}

#[cfg(test)]
mod capacity_classification_tests {
    use super::*;

    #[test]
    fn unknown_protocol_and_local_limits_are_not_plc_capacity_evidence() {
        for error in [
            Error::Protocol("GetMultiVariables rejected: return_value=0x0000000000000042".into()),
            Error::Protocol("decompressed blob exceeds the size cap".into()),
            Error::Framing("legacy chunk length exceeds telegram".into()),
            Error::Vlq("invalid encoded length".into()),
            Error::Crypto("invalid padding".into()),
        ] {
            assert_eq!(classify_operation(&error), IgwResultCode::ProtocolFailure);
        }
        assert_ne!(IgwResultCode::BufferTooSmall, IgwResultCode::CapacityFailure);
        assert_eq!(IgwResultCode::CapacityFailure as i32, 33);
    }

    #[test]
    fn transport_and_timeout_remain_separate_from_capacity() {
        assert_eq!(classify_operation(&Error::Closed("session poisoned".into())), IgwResultCode::TransportFailure);
        assert_eq!(classify_operation(&Error::Io(std::io::Error::from(std::io::ErrorKind::TimedOut))), IgwResultCode::Timeout);
    }
}

fn classify_authentication(error: &Error) -> IgwResultCode {
    let message = error.to_string().to_ascii_lowercase();
    if message.contains("access denied")
        || message.contains("legitimation rejected")
        || message.contains("authentication failed")
    {
        return IgwResultCode::AuthenticationFailed;
    }
    classify(error)
}

unsafe fn utf8(slice: IgwByteSlice) -> Result<String, IgwResultCode> {
    if slice.len == 0 { return Ok(String::new()); }
    if slice.ptr.is_null() { return Err(IgwResultCode::InvalidArgument); }
    let bytes = unsafe { slice::from_raw_parts(slice.ptr, slice.len) };
    str::from_utf8(bytes)
        .map(str::to_owned)
        .map_err(|_| IgwResultCode::InvalidArgument)
}

fn copy_output(output: IgwMutableByteSlice, bytes: &[u8]) -> IgwResult {
    let required = u32::try_from(bytes.len()).unwrap_or(u32::MAX);
    if output.len < bytes.len() {
        return IgwResult::with_detail(IgwResultCode::BufferTooSmall, required);
    }
    if !bytes.is_empty() {
        if output.ptr.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), output.ptr, bytes.len()); }
    }
    IgwResult::with_detail(IgwResultCode::Ok, required)
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_create() -> *mut IgwS7PlusHandle {
    Box::into_raw(Box::new(IgwS7PlusHandle {
        connection: Mutex::new(None),
        symbols: Mutex::new(SymbolState::default()),
        subscriptions: Mutex::new(HashMap::new()),
        next_subscription_id: AtomicU32::new(1),
        resync: Mutex::new(resync::ResyncState::default()),
    }))
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_destroy(handle: *mut IgwS7PlusHandle) {
    if handle.is_null() { return; }
    unsafe { drop(Box::from_raw(handle)); }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_connect(
    handle: *mut IgwS7PlusHandle,
    request: *const IgwConnectRequest,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| connect_inner(handle, request))) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn connect_inner(handle: *mut IgwS7PlusHandle, request: *const IgwConnectRequest) -> IgwResultCode {
    if handle.is_null() || request.is_null() { return IgwResultCode::InvalidArgument; }
    let request = unsafe { &*request };
    if request.host_ptr.is_null() || request.host_len == 0 || request.port == 0 {
        return IgwResultCode::InvalidArgument;
    }
    let host_bytes = unsafe { slice::from_raw_parts(request.host_ptr.cast::<u8>(), request.host_len) };
    let Ok(host) = str::from_utf8(host_bytes) else { return IgwResultCode::InvalidArgument; };
    let address = format!("{host}:{}", request.port);
    let endpoint = address.clone();
    let timeout = Duration::from_millis(u64::from(request.timeout_ms.max(100)));
    let connected = if request.legacy == 0 {
        Connection::connect(address, timeout)
    } else {
        Connection::connect_legacy(address, timeout)
    };
    match connected {
        Ok(connection) => {
            let handle = unsafe { &*handle };
            let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
            let Ok(mut symbols) = handle.symbols.lock() else { return IgwResultCode::Panic; };
            symbols.bind(&endpoint);
            // 订阅活在连接上：新连接意味着 PLC 侧的订阅已经没了，旧句柄不能再用
            if let Ok(mut subscriptions) = handle.subscriptions.lock() { subscriptions.clear(); }
            *slot = Some(Session::new(connection));
            IgwResultCode::Ok
        }
        Err(error) => classify(&error),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_disconnect(handle: *mut IgwS7PlusHandle) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() { return IgwResultCode::InvalidArgument; }
        let handle = unsafe { &*handle };
        let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
        if let Ok(mut subscriptions) = handle.subscriptions.lock() { subscriptions.clear(); }
        *slot = None;
        IgwResultCode::Ok
    })) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_authenticate(
    handle: *mut IgwS7PlusHandle,
    user: IgwByteSlice,
    password: IgwByteSlice,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| authenticate_inner(handle, user, password))) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn authenticate_inner(
    handle: *mut IgwS7PlusHandle,
    user: IgwByteSlice,
    password: IgwByteSlice,
) -> IgwResultCode {
    if handle.is_null() { return IgwResultCode::InvalidArgument; }
    let user = match unsafe { utf8(user) } { Ok(value) => value, Err(code) => return code };
    let password = match unsafe { utf8(password) } { Ok(value) => value, Err(code) => return code };
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
    let Some(Session { connection, .. }) = slot.as_mut() else { return IgwResultCode::TransportFailure; };
    match connection.legitimate(&user, &password) {
        Ok(()) => IgwResultCode::Ok,
        Err(error) => classify_authentication(&error),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_probe(handle: *mut IgwS7PlusHandle) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| probe_inner(handle))) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn probe_inner(handle: *mut IgwS7PlusHandle) -> IgwResultCode {
    if handle.is_null() { return IgwResultCode::InvalidArgument; }
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
    let Some(Session { connection, .. }) = slot.as_mut() else { return IgwResultCode::TransportFailure; };
    match connection.effective_protection_level() {
        Ok(_) => IgwResultCode::Ok,
        Err(error) => classify_operation(&error),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_browse(
    handle: *mut IgwS7PlusHandle,
    output: IgwMutableByteSlice,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| browse_inner(handle, output))) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn browse_inner(handle: *mut IgwS7PlusHandle, output: IgwMutableByteSlice) -> IgwResult {
    if handle.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
    let Some(Session { connection, .. }) = slot.as_mut() else { return IgwResult::new(IgwResultCode::TransportFailure); };
    let vars = match connection.browse_vars() {
        Ok(vars) => vars,
        Err(error) => return IgwResult::new(classify_operation(&error)),
    };
    let mut payload = String::new();
    for var in vars {
        let escaped = var.name.replace('\\', "\\\\").replace('\t', "\\t").replace('\n', "\\n");
        payload.push_str(&escaped);
        payload.push('\t');
        payload.push_str(&var.softdatatype.to_string());
        payload.push('\t');
        payload.push_str(&var.string_max_len.to_string());
        payload.push('\n');
    }
    copy_output(output, payload.as_bytes())
}

/// v5：列出 PLC 上的数据块(块名 → DB 号)。TIA「从块生成源」导出的 .db 源文件不带 DB 号，
/// 导入非优化 DB 时由网关按块名在线查到编号，不用人工填写。
/// 输出每行「块名 制表符 DB号 换行」，块名里的反斜杠、制表符、换行按 browse 同样的规则转义。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_list_datablocks(
    handle: *mut IgwS7PlusHandle,
    output: IgwMutableByteSlice,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| list_datablocks_inner(handle, output))) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn list_datablocks_inner(handle: *mut IgwS7PlusHandle, output: IgwMutableByteSlice) -> IgwResult {
    if handle.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
    let Some(Session { connection, .. }) = slot.as_mut() else { return IgwResult::new(IgwResultCode::TransportFailure); };
    let blocks = match connection.datablock_list() {
        Ok(blocks) => blocks,
        Err(error) => return IgwResult::new(classify_operation(&error)),
    };
    copy_output(output, datablock_payload(&blocks).as_bytes())
}

fn datablock_payload(blocks: &[s7commplus::DataBlock]) -> String {
    let mut payload = String::new();
    for block in blocks {
        let escaped = block.name.replace('\\', "\\\\").replace('\t', "\\t").replace('\n', "\\n");
        payload.push_str(&escaped);
        payload.push('\t');
        payload.push_str(&block.number.to_string());
        payload.push('\n');
    }
    payload
}

fn push_payload(result: &mut IgwReadResult, payload: &mut Vec<u8>, bytes: &[u8]) {
    result.payload_offset = u32::try_from(payload.len()).unwrap_or(u32::MAX);
    result.payload_length = u32::try_from(bytes.len()).unwrap_or(u32::MAX);
    payload.extend_from_slice(bytes);
}

fn encode_value(value: PValue, payload: &mut Vec<u8>) -> IgwReadResult {
    let mut result = IgwReadResult::default();
    match value {
        PValue::Null => {}
        PValue::Bool(value) => { result.value_type = IgwValueType::Bool; result.unsigned_value = u64::from(value); }
        PValue::USInt(value) => { result.value_type = IgwValueType::USInt; result.unsigned_value = u64::from(value); }
        PValue::UInt(value) => { result.value_type = IgwValueType::UInt; result.unsigned_value = u64::from(value); }
        PValue::UDInt(value) => { result.value_type = IgwValueType::UDInt; result.unsigned_value = u64::from(value); }
        PValue::ULInt(value) => { result.value_type = IgwValueType::ULInt; result.unsigned_value = value; }
        PValue::SInt(value) => { result.value_type = IgwValueType::SInt; result.signed_value = i64::from(value); }
        PValue::Int(value) => { result.value_type = IgwValueType::Int; result.signed_value = i64::from(value); }
        PValue::DInt(value) => { result.value_type = IgwValueType::DInt; result.signed_value = i64::from(value); }
        PValue::LInt(value) => { result.value_type = IgwValueType::LInt; result.signed_value = value; }
        PValue::Byte(value) => { result.value_type = IgwValueType::Byte; result.unsigned_value = u64::from(value); }
        PValue::Word(value) => { result.value_type = IgwValueType::Word; result.unsigned_value = u64::from(value); }
        PValue::DWord(value) => { result.value_type = IgwValueType::DWord; result.unsigned_value = u64::from(value); }
        PValue::LWord(value) => { result.value_type = IgwValueType::LWord; result.unsigned_value = value; }
        PValue::Real(value) => { result.value_type = IgwValueType::Real; result.float_value = f64::from(value); }
        PValue::LReal(value) => { result.value_type = IgwValueType::LReal; result.float_value = value; }
        PValue::Timestamp(value) => { result.value_type = IgwValueType::Timestamp; result.unsigned_value = value; }
        PValue::Timespan(value) => { result.value_type = IgwValueType::Timespan; result.signed_value = value; }
        PValue::RID(value) | PValue::AID(value) => { result.value_type = IgwValueType::UDInt; result.unsigned_value = u64::from(value); }
        PValue::WString(value) => { result.value_type = IgwValueType::Text; push_payload(&mut result, payload, value.as_bytes()); }
        PValue::Blob { data, .. } | PValue::USIntArray(data) | PValue::PackedStruct { data, .. } => {
            result.value_type = IgwValueType::Bytes;
            push_payload(&mut result, payload, &data);
        }
        PValue::Struct { .. } | PValue::Array { .. } | PValue::SparseArray { .. } => {
            result.code = IgwResultCode::ProtocolFailure;
        }
    }
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_read_batch(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwReadItem,
    count: usize,
    results: *mut IgwReadResult,
    payload_output: IgwMutableByteSlice,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| read_batch_inner(handle, items, count, results, payload_output))) {
        Ok(result) => result,
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn read_batch_inner(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwReadItem,
    count: usize,
    results: *mut IgwReadResult,
    payload_output: IgwMutableByteSlice,
) -> IgwResult {
    if handle.is_null() || (count > 0 && (items.is_null() || results.is_null())) {
        return IgwResult::new(IgwResultCode::InvalidArgument);
    }
    let input = if count == 0 { &[] } else { unsafe { slice::from_raw_parts(items, count) } };
    // 空符号或非法 UTF-8 只让该项失败(SymbolNotFound)，不再整批拒绝
    let symbols: Vec<Option<String>> = input.iter()
        .map(|item| unsafe { utf8(item.symbol) }.ok().filter(|symbol| !symbol.trim().is_empty()))
        .collect();
    let symbol_refs: Vec<Option<&str>> = symbols.iter().map(Option::as_deref).collect();
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResult::new(IgwResultCode::Panic); };
    let Some(session) = slot.as_mut() else { return IgwResult::new(IgwResultCode::TransportFailure); };
    let Ok(mut symbols) = handle.symbols.lock() else { return IgwResult::new(IgwResultCode::Panic); };
    let state = &mut *symbols;
    let mut plc = reader::PlcSession { connection: &mut session.connection, resolver: &mut state.resolver };
    let values = reader::read_symbols(&mut plc, &mut state.cache, &symbol_refs, std::time::Instant::now());
    note_rejections(handle, state);
    let values = match values {
        Ok(values) => values,
        Err(code) => return IgwResult::new(code),
    };
    let mut payload = Vec::new();
    let encoded: Vec<IgwReadResult> = values
        .into_iter()
        .map(|value| match value {
            Ok(value) => encode_value(value, &mut payload),
            Err(code) => IgwReadResult { code, ..IgwReadResult::default() },
        })
        .collect();
    if payload_output.len < payload.len() {
        return IgwResult::with_detail(IgwResultCode::BufferTooSmall, u32::try_from(payload.len()).unwrap_or(u32::MAX));
    }
    if !payload.is_empty() {
        if payload_output.ptr.is_null() { return IgwResult::new(IgwResultCode::InvalidArgument); }
        unsafe { std::ptr::copy_nonoverlapping(payload.as_ptr(), payload_output.ptr, payload.len()); }
    }
    if count > 0 {
        unsafe { std::ptr::copy_nonoverlapping(encoded.as_ptr(), results, encoded.len()); }
    }
    IgwResult::with_detail(IgwResultCode::Ok, u32::try_from(payload.len()).unwrap_or(u32::MAX))
}

fn decode_write(item: &IgwWriteItem) -> Result<PValue, IgwResultCode> {
    Ok(match item.value_type {
        IgwValueType::Null => PValue::Null,
        IgwValueType::Bool => PValue::Bool(item.unsigned_value != 0),
        IgwValueType::USInt => PValue::USInt(u8::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::UInt => PValue::UInt(u16::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::UDInt => PValue::UDInt(u32::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::ULInt => PValue::ULInt(item.unsigned_value),
        IgwValueType::SInt => PValue::SInt(i8::try_from(item.signed_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::Int => PValue::Int(i16::try_from(item.signed_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::DInt => PValue::DInt(i32::try_from(item.signed_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::LInt => PValue::LInt(item.signed_value),
        IgwValueType::Byte => PValue::Byte(u8::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::Word => PValue::Word(u16::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::DWord => PValue::DWord(u32::try_from(item.unsigned_value).map_err(|_| IgwResultCode::InvalidArgument)?),
        IgwValueType::LWord => PValue::LWord(item.unsigned_value),
        IgwValueType::Real => PValue::Real(item.float_value as f32),
        IgwValueType::LReal => PValue::LReal(item.float_value),
        IgwValueType::Timestamp => PValue::Timestamp(item.unsigned_value),
        IgwValueType::Timespan => PValue::Timespan(item.signed_value),
        IgwValueType::Text => PValue::WString(unsafe { utf8(item.payload) }?),
        IgwValueType::Bytes => {
            if item.payload.len > 0 && item.payload.ptr.is_null() { return Err(IgwResultCode::InvalidArgument); }
            let bytes = if item.payload.len == 0 { Vec::new() } else { unsafe { slice::from_raw_parts(item.payload.ptr, item.payload.len) }.to_vec() };
            PValue::USIntArray(bytes)
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_write_batch(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwWriteItem,
    count: usize,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| write_batch_inner(handle, items, count))) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

/// ABI v3：逐项写入。`results` 与 `items` 等长，逐项给出结果；返回值只在整批失败(断线/超时/报文整体被拒)时非 Ok。
#[unsafe(no_mangle)]
pub extern "C" fn igw_s7plus_write_items(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwWriteItem,
    count: usize,
    results: *mut IgwResultCode,
) -> IgwResult {
    match catch_unwind(AssertUnwindSafe(|| write_items_inner(handle, items, count, results))) {
        Ok(code) => IgwResult::new(code),
        Err(_) => IgwResult::new(IgwResultCode::Panic),
    }
}

fn write_items_inner(
    handle: *mut IgwS7PlusHandle,
    items: *const IgwWriteItem,
    count: usize,
    results: *mut IgwResultCode,
) -> IgwResultCode {
    if handle.is_null() || (count > 0 && (items.is_null() || results.is_null())) { return IgwResultCode::InvalidArgument; }
    let input = if count == 0 { &[] } else { unsafe { slice::from_raw_parts(items, count) } };
    // 符号为空/非法 UTF-8 或值无法编码：只让该项失败
    let decoded: Vec<Result<(String, PValue), IgwResultCode>> = input.iter()
        .map(|item| {
            let symbol = match unsafe { utf8(item.symbol) } {
                Ok(symbol) if !symbol.trim().is_empty() => symbol,
                _ => return Err(IgwResultCode::SymbolNotFound),
            };
            Ok((symbol, decode_write(item)?))
        })
        .collect();
    let pairs: Vec<Result<(&str, PValue), IgwResultCode>> = decoded.iter()
        .map(|item| item.as_ref().map(|(symbol, value)| (symbol.as_str(), value.clone())).map_err(|code| *code))
        .collect();
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
    let Some(session) = slot.as_mut() else { return IgwResultCode::TransportFailure; };
    let Ok(mut symbols) = handle.symbols.lock() else { return IgwResultCode::Panic; };
    let state = &mut *symbols;
    let mut plc = reader::PlcSession { connection: &mut session.connection, resolver: &mut state.resolver };
    let written = reader::write_symbol_items(&mut plc, &mut state.cache, &pairs, std::time::Instant::now());
    note_rejections(handle, state);
    match written {
        Ok(codes) => {
            if count > 0 { unsafe { std::ptr::copy_nonoverlapping(codes.as_ptr(), results, codes.len()); } }
            IgwResultCode::Ok
        }
        Err(code) => code,
    }
}

fn write_batch_inner(handle: *mut IgwS7PlusHandle, items: *const IgwWriteItem, count: usize) -> IgwResultCode {
    if handle.is_null() || (count > 0 && items.is_null()) { return IgwResultCode::InvalidArgument; }
    let input = if count == 0 { &[] } else { unsafe { slice::from_raw_parts(items, count) } };
    let mut symbols = Vec::with_capacity(count);
    let mut values = Vec::with_capacity(count);
    for item in input {
        match unsafe { utf8(item.symbol) } {
            Ok(symbol) if !symbol.trim().is_empty() => symbols.push(symbol),
            _ => return IgwResultCode::InvalidArgument,
        }
        match decode_write(item) {
            Ok(value) => values.push(value),
            Err(code) => return code,
        }
    }
    let pairs: Vec<(&str, PValue)> = symbols.iter().map(String::as_str).zip(values).collect();
    let handle = unsafe { &*handle };
    let Ok(mut slot) = handle.connection.lock() else { return IgwResultCode::Panic; };
    let Some(session) = slot.as_mut() else { return IgwResultCode::TransportFailure; };
    let Ok(mut symbols) = handle.symbols.lock() else { return IgwResultCode::Panic; };
    let state = &mut *symbols;
    let mut plc = reader::PlcSession { connection: &mut session.connection, resolver: &mut state.resolver };
    let written = reader::write_symbols(&mut plc, &mut state.cache, &pairs, std::time::Instant::now());
    note_rejections(handle, state);
    match written {
        Ok(()) => IgwResultCode::Ok,
        Err(code) => code,
    }
}

#[cfg(test)]
mod datablock_payload_tests {
    use super::*;

    #[test]
    fn lists_block_names_with_their_numbers_and_escapes_separators() {
        let blocks = vec![
            s7commplus::DataBlock { name: "ST430_Data Collection_A(数据收集)".into(), relid: 0x8a0e_10d2, number: 4306, ti_relid: 1 },
            s7commplus::DataBlock { name: "A\tB\\C".into(), relid: 0x8a0e_0001, number: 1, ti_relid: 2 },
        ];
        assert_eq!(datablock_payload(&blocks), "ST430_Data Collection_A(数据收集)\t4306\nA\\tB\\\\C\t1\n");
    }
}

#[cfg(test)]
mod symbol_state_tests {
    use super::*;

    fn cached(endpoint: &str) -> SymbolState {
        let mut state = SymbolState::default();
        state.bind(endpoint);
        state.cache.insert_resolved("DB110_OP010_User.Cmd.CloseStation1");
        assert_eq!(state.cache.len(), 1);
        state
    }

    /// 32 万点的设备重连一次就重新解析全部符号的话，开销会落在单次读取的超时里并自锁，
    /// 所以同一台 PLC 的重连必须原样保留缓存。
    #[test]
    fn reconnecting_to_the_same_plc_keeps_the_cache() {
        let mut state = cached("10.11.38.10:102");
        state.bind("10.11.38.10:102");
        assert_eq!(state.cache.len(), 1);
    }

    /// 换了 PLC 就不能再用上一台的地址。
    #[test]
    fn connecting_to_another_plc_drops_the_cache() {
        let mut state = cached("10.11.38.10:102");
        state.bind("10.11.38.20:102");
        assert_eq!(state.cache.len(), 0);
        assert_eq!(state.endpoint, "10.11.38.20:102");
    }

    /// 判定 PLC 重新下载了程序(同一 DB 内连续 3 次已解析地址被拒)时，缓存必须整体作废。
    #[test]
    fn a_program_download_resync_clears_the_cache() {
        let handle = IgwS7PlusHandle {
            connection: Mutex::new(None),
            symbols: Mutex::new(SymbolState::default()),
            subscriptions: Mutex::new(HashMap::new()),
            next_subscription_id: AtomicU32::new(1),
            resync: Mutex::new(resync::ResyncState::default()),
        };
        let mut state = cached("10.11.38.10:102");
        state.cache.rejected = vec![
            "DB110_OP010_User.Cmd.A".to_owned(),
            "DB110_OP010_User.Cmd.B".to_owned(),
            "DB110_OP010_User.Cmd.C".to_owned(),
        ];
        note_rejections(&handle, &mut state);
        assert_eq!(state.cache.len(), 0, "程序变更自愈要清空缓存");
    }

    /// 没到自愈阈值的零星拒绝不能把整份缓存清掉。
    #[test]
    fn a_single_rejected_symbol_keeps_the_cache() {
        let handle = IgwS7PlusHandle {
            connection: Mutex::new(None),
            symbols: Mutex::new(SymbolState::default()),
            subscriptions: Mutex::new(HashMap::new()),
            next_subscription_id: AtomicU32::new(1),
            resync: Mutex::new(resync::ResyncState::default()),
        };
        let mut state = cached("10.11.38.10:102");
        state.cache.rejected = vec!["DB110_OP010_User.Cmd.A".to_owned()];
        note_rejections(&handle, &mut state);
        assert_eq!(state.cache.len(), 1);
    }
}
