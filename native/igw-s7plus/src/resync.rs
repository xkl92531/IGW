//! PLC 程序重新下载后的自愈判断。
//!
//! 连接期间 PLC 下载了新程序（DB 结构变了）时，按符号缓存的地址和 crate 自身的类型缓存都会过期，
//! 而 crate 的类型缓存只有重建连接才能清空。这里根据读写时的单项错误判断是否需要重建连接：
//! 同一个 DB（或区）在 60 秒内对「之前解析成功过的地址」累计出现 3 次单项错误，就请求重建。
//! 重建由托管侧执行（断开后按正常流程重连并重新认证），这里只给出信号。
//!
//! 防止反复重建（每次重建都要重新解析全部符号）：
//! - 两次重建至少间隔 30 分钟；
//! - 重建后那些触发过重建的符号若仍然出错，视为真正的坏点，不再计数（它们由网关的坏点隔离处理）。

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

pub const WINDOW: Duration = Duration::from_secs(60);
pub const THRESHOLD: usize = 3;
pub const MIN_INTERVAL: Duration = Duration::from_secs(30 * 60);

/// 与连接无关、跨重连保留的状态（挂在 handle 上）。
#[derive(Default)]
pub struct ResyncState {
    errors: HashMap<String, Vec<(Instant, String)>>,
    last_resync: Option<Instant>,
    triggered_by: HashSet<String>,
    excluded: HashSet<String>,
    requested: bool,
}

/// 符号所属的 DB 或区：第一段（去掉数组下标）。
pub fn area_of(symbol: &str) -> &str {
    let first = symbol.split('.').next().unwrap_or(symbol);
    first.split('[').next().unwrap_or(first).trim()
}

impl ResyncState {
    /// 记录一次「已解析地址被 PLC 拒绝」。返回是否刚刚发出了重建请求。
    pub fn record_item_error(&mut self, symbol: &str, now: Instant) -> bool {
        if self.excluded.contains(symbol) {
            return false;
        }
        if self.triggered_by.contains(symbol) {
            // 重建后仍然出错：不是程序变化造成的，是真正的坏点
            self.excluded.insert(symbol.to_owned());
            return false;
        }
        let area = area_of(symbol).to_owned();
        let list = self.errors.entry(area).or_default();
        list.retain(|(at, _)| now.duration_since(*at) <= WINDOW);
        list.push((now, symbol.to_owned()));
        if list.len() < THRESHOLD {
            return false;
        }
        if self.last_resync.is_some_and(|last| now.duration_since(last) < MIN_INTERVAL) {
            return false;
        }
        self.triggered_by = list.iter().map(|(_, symbol)| symbol.clone()).collect();
        self.errors.clear();
        self.last_resync = Some(now);
        self.requested = true;
        true
    }

    /// 取出并清除重建请求。
    pub fn take_request(&mut self) -> bool {
        std::mem::take(&mut self.requested)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn three_errors_in_one_db_within_a_minute_request_a_resync() {
        let mut state = ResyncState::default();
        let now = Instant::now();
        assert!(!state.record_item_error("DB1.A", now));
        assert!(!state.record_item_error("DB2.X", now), "不同 DB 分开计数");
        assert!(!state.record_item_error("DB1.B", now));
        assert!(state.record_item_error("DB1.C[2].D", now));
        assert!(state.take_request());
        assert!(!state.take_request(), "请求取出后清除");
    }

    #[test]
    fn errors_outside_the_window_do_not_add_up() {
        let mut state = ResyncState::default();
        let start = Instant::now();
        state.record_item_error("DB1.A", start);
        state.record_item_error("DB1.B", start);
        assert!(!state.record_item_error("DB1.C", start + WINDOW + Duration::from_secs(1)));
    }

    #[test]
    fn resyncs_are_rate_limited_and_symbols_still_failing_afterwards_stop_counting() {
        let mut state = ResyncState::default();
        let start = Instant::now();
        for symbol in ["DB1.A", "DB1.B", "DB1.C"] { state.record_item_error(symbol, start); }
        assert!(state.take_request());
        // 重建后同样三个符号仍然出错：它们是真正的坏点，不再触发
        let later = start + MIN_INTERVAL + Duration::from_secs(1);
        for symbol in ["DB1.A", "DB1.B", "DB1.C", "DB1.A", "DB1.B", "DB1.C"] {
            assert!(!state.record_item_error(symbol, later));
        }
        // 30 分钟内其它符号再出错也不重建
        let mut fresh = ResyncState::default();
        for symbol in ["DB1.A", "DB1.B", "DB1.C"] { fresh.record_item_error(symbol, start); }
        fresh.take_request();
        for symbol in ["DB1.X", "DB1.Y", "DB1.Z"] {
            assert!(!fresh.record_item_error(symbol, start + Duration::from_secs(60)));
        }
    }

    #[test]
    fn area_is_the_first_segment_without_indices() {
        assert_eq!(area_of("DB2100_电芯.CellData[0].DMC"), "DB2100_电芯");
        assert_eq!(area_of("Arr[3].X"), "Arr");
        assert_eq!(area_of("M"), "M");
    }
}
