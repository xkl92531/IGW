//! 按符号读取：符号解析缓存 + 单项失败隔离。
//!
//! s7commplus 的 `read_tags` 每次都完整解析每个符号（命中缓存也会复制整个 DB 列表和各层类型对象），
//! 任一符号解析失败则整批失败。这里改为：
//! - 每个符号只解析一次，结果（含失败，带过期时间）缓存在会话上，与连接同寿命；
//! - 解析失败或 PLC 对单项报错只影响该项（`SymbolNotFound`），其余项照常读取；
//! - 只把解析成功的项发给 PLC，结果按 item 序号 O(1) 回填。

use std::collections::HashMap;
use std::time::{Duration, Instant};

use s7commplus::proto::ItemAddress;
use s7commplus::value::PValue;
use s7commplus::{Connection, Error};

use crate::IgwResultCode;
use crate::resolver::{ResolveError, Resolver};

/// 解析失败的缓存时长：期间不再向 PLC 发 Explore；与网关坏点隔离的复探周期(30 s)一致。
pub const NEGATIVE_TTL: Duration = Duration::from_secs(30);

/// 读取所需的 PLC 操作，便于脱离 PLC 测试。
pub trait SymbolSource {
    fn resolve(&mut self, symbol: &str) -> Result<ItemAddress, Error>;
    /// 返回 (成功的 (item_no, value), 单项错误 (item_no, return_value))，item_no 从 1 开始。
    fn read(&mut self, addresses: &[ItemAddress]) -> Result<(Vec<(u32, PValue)>, Vec<(u32, u64)>), Error>;
    /// 返回单项错误 (item_no, return_value)。
    fn write(&mut self, addresses: &[ItemAddress], values: &[PValue]) -> Result<Vec<(u32, u64)>, Error>;
}

/// 真实 PLC：解析走会话上的 [`Resolver`](每个类型只取一次)，读写走连接。
pub struct PlcSession<'a> {
    pub connection: &'a mut Connection,
    pub resolver: &'a mut Resolver,
}

impl SymbolSource for PlcSession<'_> {
    fn resolve(&mut self, symbol: &str) -> Result<ItemAddress, Error> {
        match self.resolver.resolve(self.connection, symbol) {
            Ok(address) => Ok(address),
            Err(ResolveError::Plc(error)) => Err(error),
            Err(ResolveError::NotFound(message)) => Err(Error::Protocol(message)),
        }
    }

    fn read(&mut self, addresses: &[ItemAddress]) -> Result<(Vec<(u32, PValue)>, Vec<(u32, u64)>), Error> {
        let response = self.connection.read_variables(addresses)?;
        Ok((response.values, response.errors))
    }

    fn write(&mut self, addresses: &[ItemAddress], values: &[PValue]) -> Result<Vec<(u32, u64)>, Error> {
        Ok(self.connection.write_variables(addresses, values)?.errors)
    }
}

enum CacheEntry {
    Resolved(ItemAddress),
    Missing { until: Instant },
}

#[derive(Default)]
pub struct SymbolCache {
    entries: HashMap<String, CacheEntry>,
    /// 本次调用中已解析地址被 PLC 拒绝的符号(供程序变更自愈判断，调用方每次取走)。
    pub rejected: Vec<String>,
}

impl SymbolCache {
    #[cfg(test)]
    pub fn len(&self) -> usize { self.entries.len() }

    /// 测试用：直接塞一条已解析的地址，免得为了造缓存去跑一遍解析。
    #[cfg(test)]
    pub fn insert_resolved(&mut self, symbol: &str) {
        self.entries.insert(symbol.to_owned(), CacheEntry::Resolved(ItemAddress {
            symbol_crc: 0, access_area: 1, access_sub_area: 2, lid: vec![1],
        }));
    }
}

enum Lookup {
    Found(ItemAddress),
    Missing,
}

/// 查缓存，未命中才向 PLC 解析。连接断开/超时(解析要发 Explore)整批上抛，不记成“符号不存在”。
fn lookup<S: SymbolSource>(source: &mut S, cache: &mut SymbolCache, symbol: &str, now: Instant) -> Result<Lookup, IgwResultCode> {
    match cache.entries.get(symbol) {
        Some(CacheEntry::Resolved(address)) => return Ok(Lookup::Found(address.clone())),
        Some(CacheEntry::Missing { until }) if *until > now => return Ok(Lookup::Missing),
        _ => {}
    }
    match source.resolve(symbol) {
        Ok(address) => {
            cache.entries.insert(symbol.to_owned(), CacheEntry::Resolved(address.clone()));
            Ok(Lookup::Found(address))
        }
        Err(error) if error.is_connection_lost() || error.is_timeout() => Err(crate::classify(&error)),
        Err(_) => {
            cache.entries.insert(symbol.to_owned(), CacheEntry::Missing { until: now + NEGATIVE_TTL });
            Ok(Lookup::Missing)
        }
    }
}

/// 订阅路径复用同一份解析缓存：解析到给出地址，解析不到给出 None(只影响该项)。
pub fn lookup_address<S: SymbolSource>(
    source: &mut S,
    cache: &mut SymbolCache,
    symbol: &str,
    now: Instant,
) -> Result<Option<ItemAddress>, IgwResultCode> {
    match lookup(source, cache, symbol, now)? {
        Lookup::Found(address) => Ok(Some(address)),
        Lookup::Missing => Ok(None),
    }
}

/// 整批结果：Err 表示整批失败(连接断开/超时/报文被整体拒绝)，否则每项一个结果。
pub type BatchOutcome = Result<Vec<Result<PValue, IgwResultCode>>, IgwResultCode>;

/// `symbols` 中 None 表示调用方给的符号为空或非法，按单项 `SymbolNotFound` 返回。
pub fn read_symbols<S: SymbolSource>(
    source: &mut S,
    cache: &mut SymbolCache,
    symbols: &[Option<&str>],
    now: Instant,
) -> BatchOutcome {
    let mut results: Vec<Result<PValue, IgwResultCode>> = vec![Err(IgwResultCode::SymbolNotFound); symbols.len()];
    let mut addresses = Vec::with_capacity(symbols.len());
    let mut positions = Vec::with_capacity(symbols.len());
    for (index, symbol) in symbols.iter().enumerate() {
        let Some(symbol) = symbol else { continue };
        if let Lookup::Found(address) = lookup(source, cache, symbol, now)? {
            addresses.push(address);
            positions.push(index);
        }
    }
    if addresses.is_empty() {
        return Ok(results);
    }

    let (values, errors) = match source.read(&addresses) {
        Ok(response) => response,
        Err(error) if error.is_connection_lost() || error.is_timeout() => return Err(crate::classify(&error)),
        // 报文头被整体拒绝：多项时交给调用方缩小批量重试(可能是超出 PLC 单次容量)，单项时只标该项失败
        Err(_) if addresses.len() > 1 => return Err(IgwResultCode::CapacityFailure),
        Err(error) => {
            results[positions[0]] = Err(crate::classify_operation(&error));
            return Ok(results);
        }
    };
    // 多项请求一个值都没回来：PLC 可能以“每项都报错”的方式拒绝超出容量的报文(同 crate read_chunk_adaptive)。
    // 交给调用方缩小批量重试，不能把整批好点都记成“符号不存在”而被隔离；真正的坏点缩到单项后再按单项报告。
    if addresses.len() > 1 && values.is_empty() {
        return Err(IgwResultCode::CapacityFailure);
    }
    for position in &positions {
        results[*position] = Err(IgwResultCode::ProtocolFailure);   // PLC 既没回值也没报错
    }
    for (item, value) in values {
        if let Some(position) = slot(&positions, item) {
            results[position] = Ok(value);
        }
    }
    for (item, _) in errors {
        if let Some(position) = slot(&positions, item) {
            results[position] = Err(IgwResultCode::SymbolNotFound);
            // 地址可能已随 PLC 程序变化失效：下次重新解析(crate 自身的类型缓存要重连才会刷新)
            if let Some(symbol) = symbols[position] {
                cache.entries.remove(symbol);
                cache.rejected.push(symbol.to_owned());
            }
        }
    }
    Ok(results)
}

/// 按符号写入(一个 SetMultiVariables)：解析走同一个缓存。
/// 写入保持整批语义：任一符号找不到或 PLC 拒绝任一项，整批返回失败(单项写入隔离要扩展写入 ABI，本次不做)；
/// 被 PLC 拒绝的项作废其缓存地址，下次重新解析。
pub fn write_symbols<S: SymbolSource>(
    source: &mut S,
    cache: &mut SymbolCache,
    pairs: &[(&str, PValue)],
    now: Instant,
) -> Result<(), IgwResultCode> {
    let mut addresses = Vec::with_capacity(pairs.len());
    let mut values = Vec::with_capacity(pairs.len());
    for (symbol, value) in pairs {
        match lookup(source, cache, symbol, now)? {
            Lookup::Found(address) => addresses.push(address),
            Lookup::Missing => return Err(IgwResultCode::SymbolNotFound),
        }
        values.push(value.clone());
    }
    if addresses.is_empty() {
        return Ok(());
    }
    let errors = match source.write(&addresses, &values) {
        Ok(errors) => errors,
        Err(error) => return Err(crate::classify_operation(&error)),
    };
    if errors.is_empty() {
        return Ok(());
    }
    for (item, _) in &errors {
        let index = usize::try_from(*item).ok().and_then(|item| item.checked_sub(1));
        if let Some((symbol, _)) = index.and_then(|index| pairs.get(index)) {
            cache.entries.remove(*symbol);
        }
    }
    Err(IgwResultCode::ProtocolFailure)
}

/// 逐项写入(ABI v3)：找不到的符号只让该项失败(SymbolNotFound)，其余项照常发给 PLC；
/// PLC 拒绝的项为 ItemRejected 并作废其缓存地址。`symbols` 中 None 表示调用方给的符号或值非法(按 `invalid` 返回)。
/// 整批失败只在连接断开/超时，或多项报文被整体拒绝(CapacityFailure，调用方缩小批量重试)时发生。
pub fn write_symbol_items<S: SymbolSource>(
    source: &mut S,
    cache: &mut SymbolCache,
    items: &[Result<(&str, PValue), IgwResultCode>],
    now: Instant,
) -> Result<Vec<IgwResultCode>, IgwResultCode> {
    let mut results: Vec<IgwResultCode> = items.iter()
        .map(|item| match item { Ok(_) => IgwResultCode::Ok, Err(code) => *code })
        .collect();
    let mut addresses = Vec::with_capacity(items.len());
    let mut values = Vec::with_capacity(items.len());
    let mut positions = Vec::with_capacity(items.len());
    for (index, item) in items.iter().enumerate() {
        let Ok((symbol, value)) = item else { continue };
        match lookup(source, cache, symbol, now)? {
            Lookup::Found(address) => {
                addresses.push(address);
                values.push(value.clone());
                positions.push(index);
            }
            Lookup::Missing => results[index] = IgwResultCode::SymbolNotFound,
        }
    }
    if addresses.is_empty() {
        return Ok(results);
    }
    let errors = match source.write(&addresses, &values) {
        Ok(errors) => errors,
        Err(error) if error.is_connection_lost() || error.is_timeout() => return Err(crate::classify(&error)),
        Err(_) if addresses.len() > 1 => return Err(IgwResultCode::CapacityFailure),
        Err(error) => {
            results[positions[0]] = crate::classify_operation(&error);
            return Ok(results);
        }
    };
    for (item, _) in errors {
        if let Some(position) = slot(&positions, item) {
            results[position] = IgwResultCode::ItemRejected;
            if let Ok((symbol, _)) = &items[position] {
                cache.entries.remove(*symbol);
                cache.rejected.push((*symbol).to_owned());
            }
        }
    }
    Ok(results)
}

fn slot(positions: &[usize], item: u32) -> Option<usize> {
    let index = usize::try_from(item).ok()?.checked_sub(1)?;
    positions.get(index).copied()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct FakePlc {
        missing: Vec<&'static str>,
        rejected: Vec<&'static str>,
        resolve_calls: usize,
        read_calls: usize,
        write_calls: usize,
        reverse: bool,
        read_error: Option<fn() -> Error>,
        resolve_error: Option<fn() -> Error>,
        write_error: Option<fn() -> Error>,
    }

    fn address_of(symbol: &str) -> ItemAddress {
        ItemAddress { symbol_crc: 0, access_area: symbol.len() as u32, access_sub_area: 0, lid: symbol.bytes().map(u32::from).collect() }
    }

    fn symbol_of(address: &ItemAddress) -> String { address.lid.iter().map(|b| *b as u8 as char).collect() }

    impl SymbolSource for FakePlc {
        fn resolve(&mut self, symbol: &str) -> Result<ItemAddress, Error> {
            self.resolve_calls += 1;
            if let Some(error) = self.resolve_error { return Err(error()); }
            if self.missing.contains(&symbol) { return Err(Error::Protocol(format!("unknown member {symbol}"))); }
            Ok(address_of(symbol))
        }

        fn read(&mut self, addresses: &[ItemAddress]) -> Result<(Vec<(u32, PValue)>, Vec<(u32, u64)>), Error> {
            self.read_calls += 1;
            if let Some(error) = self.read_error { return Err(error()); }
            let mut values = Vec::new();
            let mut errors = Vec::new();
            for (index, address) in addresses.iter().enumerate() {
                let item = index as u32 + 1;
                let symbol = symbol_of(address);
                if self.rejected.contains(&symbol.as_str()) { errors.push((item, 0x8104_0000)); }
                else { values.push((item, PValue::DInt(symbol.len() as i32))); }
            }
            if self.reverse { values.reverse(); }
            Ok((values, errors))
        }

        fn write(&mut self, addresses: &[ItemAddress], _values: &[PValue]) -> Result<Vec<(u32, u64)>, Error> {
            self.write_calls += 1;
            if let Some(error) = self.write_error { return Err(error()); }
            Ok(addresses.iter().enumerate()
                .filter(|(_, address)| self.rejected.contains(&symbol_of(address).as_str()))
                .map(|(index, _)| (index as u32 + 1, 0x8104_0000))
                .collect())
        }
    }

    fn ok_count(outcome: &BatchOutcome) -> usize { outcome.as_ref().unwrap().iter().filter(|r| r.is_ok()).count() }

    #[test]
    fn one_bad_symbol_only_fails_itself() {
        let mut plc = FakePlc { missing: vec!["DB1.Bad"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let symbols = [Some("DB1.A"), Some("DB1.Bad"), Some("DB1.Bcd"), None];
        let outcome = read_symbols(&mut plc, &mut cache, &symbols, Instant::now());
        let results = outcome.as_ref().unwrap();
        assert!(matches!(results[0], Ok(PValue::DInt(5))));
        assert_eq!(results[1], Err(IgwResultCode::SymbolNotFound));
        assert!(matches!(results[2], Ok(PValue::DInt(7))));
        assert_eq!(results[3], Err(IgwResultCode::SymbolNotFound));
        assert_eq!(plc.read_calls, 1);
    }

    #[test]
    fn resolved_symbols_are_cached_and_failures_are_retried_after_ttl() {
        let mut plc = FakePlc { missing: vec!["M.Bad"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let symbols = [Some("DB1.A"), Some("M.Bad")];
        let start = Instant::now();
        read_symbols(&mut plc, &mut cache, &symbols, start).unwrap();
        assert_eq!(plc.resolve_calls, 2);
        read_symbols(&mut plc, &mut cache, &symbols, start + Duration::from_secs(1)).unwrap();
        assert_eq!(plc.resolve_calls, 2, "命中缓存(含失败结果)时不再解析");
        read_symbols(&mut plc, &mut cache, &symbols, start + NEGATIVE_TTL + Duration::from_secs(1)).unwrap();
        assert_eq!(plc.resolve_calls, 3, "失败结果过期后只重试失败的符号");
    }

    #[test]
    fn results_are_placed_by_item_number_in_any_order() {
        let mut plc = FakePlc { reverse: true, missing: vec!["DB1.X"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let symbols = [Some("DB1.A"), Some("DB1.X"), Some("DB1.Bcd"), Some("DB1.Efgh")];
        let outcome = read_symbols(&mut plc, &mut cache, &symbols, Instant::now());
        let results = outcome.unwrap();
        assert!(matches!(results[0], Ok(PValue::DInt(5))));
        assert!(matches!(results[2], Ok(PValue::DInt(7))));
        assert!(matches!(results[3], Ok(PValue::DInt(8))));
    }

    #[test]
    fn plc_item_errors_fail_the_item_and_drop_its_cached_address() {
        let mut plc = FakePlc { rejected: vec!["DB1.Gone"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let symbols = [Some("DB1.A"), Some("DB1.Gone")];
        let outcome = read_symbols(&mut plc, &mut cache, &symbols, Instant::now());
        assert_eq!(ok_count(&outcome), 1);
        assert_eq!(outcome.unwrap()[1], Err(IgwResultCode::SymbolNotFound));
        assert_eq!(cache.len(), 1, "被 PLC 拒绝的地址下次重新解析");
        read_symbols(&mut plc, &mut cache, &symbols, Instant::now()).unwrap();
        assert_eq!(plc.resolve_calls, 3);
    }

    #[test]
    fn a_multi_item_request_where_every_item_errors_asks_the_caller_to_shrink() {
        let mut plc = FakePlc { rejected: vec!["DB1.A", "DB1.B"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let outcome = read_symbols(&mut plc, &mut cache, &[Some("DB1.A"), Some("DB1.B")], Instant::now());
        assert_eq!(outcome.unwrap_err(), IgwResultCode::CapacityFailure, "不能把整批都记成符号不存在");
        let single = read_symbols(&mut plc, &mut cache, &[Some("DB1.A")], Instant::now());
        assert_eq!(single.unwrap()[0], Err(IgwResultCode::SymbolNotFound), "缩到单项后才按单项报告");
    }

    #[test]
    fn writes_reuse_the_resolve_cache_and_report_missing_symbols() {
        let mut plc = FakePlc { missing: vec!["DB1.Bad"], rejected: vec!["DB1.Locked"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let now = Instant::now();
        read_symbols(&mut plc, &mut cache, &[Some("DB1.A")], now).unwrap();
        assert_eq!(write_symbols(&mut plc, &mut cache, &[("DB1.A", PValue::Int(1))], now), Ok(()));
        assert_eq!(plc.resolve_calls, 1, "写入复用读取时的解析结果");
        assert_eq!(write_symbols(&mut plc, &mut cache, &[("DB1.A", PValue::Int(1)), ("DB1.Bad", PValue::Int(2))], now),
            Err(IgwResultCode::SymbolNotFound));
        assert_eq!(plc.write_calls, 1, "有找不到的符号时整批不发");
        assert_eq!(write_symbols(&mut plc, &mut cache, &[("DB1.Locked", PValue::Int(3))], now), Err(IgwResultCode::ProtocolFailure));
        assert_eq!(cache.len(), 2, "被拒绝的地址作废，下次重新解析");
    }

    #[test]
    fn item_writes_only_fail_the_missing_or_rejected_items() {
        let mut plc = FakePlc { missing: vec!["DB1.Bad"], rejected: vec!["DB1.Locked"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let items = [Ok(("DB1.A", PValue::Int(1))), Ok(("DB1.Bad", PValue::Int(2))),
            Err(IgwResultCode::InvalidArgument), Ok(("DB1.Locked", PValue::Int(3))), Ok(("DB1.Bcd", PValue::Int(4)))];
        let results = write_symbol_items(&mut plc, &mut cache, &items, Instant::now()).unwrap();
        assert_eq!(results, vec![IgwResultCode::Ok, IgwResultCode::SymbolNotFound, IgwResultCode::InvalidArgument,
            IgwResultCode::ItemRejected, IgwResultCode::Ok]);
        assert_eq!(plc.write_calls, 1, "找不到的项不发，其余项一次写入");
        assert_eq!(cache.len(), 3, "被拒绝的地址作废(DB1.A、DB1.Bcd 和 DB1.Bad 的失败结果保留)");
    }

    #[test]
    fn item_writes_fail_the_batch_only_for_session_errors_or_whole_request_rejection() {
        let mut plc = FakePlc { resolve_error: Some(|| Error::Closed("gone".into())), ..Default::default() };
        let mut cache = SymbolCache::default();
        let items = [Ok(("DB1.A", PValue::Int(1)))];
        assert_eq!(write_symbol_items(&mut plc, &mut cache, &items, Instant::now()), Err(IgwResultCode::TransportFailure));
        let mut plc = FakePlc { write_error: Some(|| Error::Protocol("rejected".into())), ..Default::default() };
        let two = [Ok(("DB1.A", PValue::Int(1))), Ok(("DB1.B", PValue::Int(2)))];
        assert_eq!(write_symbol_items(&mut plc, &mut cache, &two, Instant::now()), Err(IgwResultCode::CapacityFailure));
    }

    #[test]
    fn nothing_is_sent_when_no_symbol_resolves() {
        let mut plc = FakePlc { missing: vec!["M.X"], ..Default::default() };
        let mut cache = SymbolCache::default();
        let outcome = read_symbols(&mut plc, &mut cache, &[Some("M.X"), None], Instant::now());
        assert_eq!(ok_count(&outcome), 0);
        assert_eq!(plc.read_calls, 0);
    }

    #[test]
    fn request_rejection_asks_the_caller_to_shrink_only_for_multiple_items() {
        let mut plc = FakePlc { read_error: Some(|| Error::Protocol("GetMultiVariables rejected".into())), ..Default::default() };
        let mut cache = SymbolCache::default();
        let outcome = read_symbols(&mut plc, &mut cache, &[Some("DB1.A"), Some("DB1.B")], Instant::now());
        assert_eq!(outcome.unwrap_err(), IgwResultCode::CapacityFailure);
        let single = read_symbols(&mut plc, &mut cache, &[Some("DB1.A")], Instant::now());
        assert_eq!(single.unwrap()[0], Err(IgwResultCode::ProtocolFailure));
    }

    #[test]
    fn connection_loss_and_timeouts_fail_the_whole_batch_without_caching_misses() {
        let mut plc = FakePlc { resolve_error: Some(|| Error::Closed("session poisoned".into())), ..Default::default() };
        let mut cache = SymbolCache::default();
        let outcome = read_symbols(&mut plc, &mut cache, &[Some("DB1.A")], Instant::now());
        assert_eq!(outcome.unwrap_err(), IgwResultCode::TransportFailure);
        assert_eq!(cache.len(), 0);

        let mut plc = FakePlc { read_error: Some(|| Error::Io(std::io::Error::from(std::io::ErrorKind::TimedOut))), ..Default::default() };
        let outcome = read_symbols(&mut plc, &mut cache, &[Some("DB1.A"), Some("DB1.B")], Instant::now());
        assert_eq!(outcome.unwrap_err(), IgwResultCode::Timeout);
    }
}
