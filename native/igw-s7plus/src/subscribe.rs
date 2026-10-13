//! S7CommPlus 变化订阅：PLC 按周期主动推送变化的值，客户端不再轮询。
//!
//! 与读取共用同一条连接和同一份符号解析缓存（见 `reader::SymbolCache`）：
//! 订阅只是把解析出来的 `ItemAddress` 交给 PLC 登记，之后由 `poll` 取回通知。
//!
//! 解析不了的符号只影响它自己（该项返回 `SymbolNotFound`，其余照常订阅），
//! 和读取路径的逐项隔离保持一致——一个配错的点位不能让整个订阅建不起来。

use std::collections::HashMap;
use std::time::Instant;

use s7commplus::proto::{ItemAddress, SubscriptionItem};
use s7commplus::value::PValue;
use s7commplus::{Connection, Subscription};

use crate::IgwResultCode;
use crate::reader::{self, SymbolCache};
use crate::resolver::Resolver;

/// 一个已建立的订阅：PLC 侧的对象 id + 客户端引用号到调用方项序号的映射。
pub struct ActiveSubscription {
    pub subscription: Subscription,
    /// reference_id → 调用方传入的项序号（通知按 reference_id 回传值）。
    slots: HashMap<u32, usize>,
    /// 订阅的点数，供诊断。
    pub item_count: usize,
}


/// 订阅前的准备：把符号解析成地址，给每项分配 PLC 回传用的引用号。
/// 抽成纯函数是为了能脱开 PLC 测试——`Subscription` 的字段是私有的，伪造不出来。
///
/// 返回 (要发给 PLC 的项, 引用号→项序号, 每项结果)。解析不到的符号只影响它自己。
pub fn plan(
    resolved: impl Fn(usize, &str) -> Option<ItemAddress>,
    symbols: &[Option<&str>],
) -> (Vec<SubscriptionItem>, HashMap<u32, usize>, Vec<Result<(), IgwResultCode>>) {
    let mut results: Vec<Result<(), IgwResultCode>> =
        vec![Err(IgwResultCode::SymbolNotFound); symbols.len()];
    let mut items = Vec::with_capacity(symbols.len());
    let mut slots = HashMap::with_capacity(symbols.len());
    for (index, symbol) in symbols.iter().enumerate() {
        let Some(symbol) = symbol else { continue };
        let Some(address) = resolved(index, symbol) else { continue };
        // 引用号用项序号 +1：PLC 原样回传，通知里据此对回调用方的点位
        let reference_id = index as u32 + 1;
        slots.insert(reference_id, index);
        items.push(SubscriptionItem { reference_id, address });
        results[index] = Ok(());
    }
    (items, slots, results)
}

/// 把一个通知里的值映射回调用方的项序号。不认识的引用号直接丢弃(不是我们订的)。
pub fn map_values(
    values: Vec<(u32, PValue)>,
    slots: &HashMap<u32, usize>,
) -> Vec<(usize, PValue)> {
    values.into_iter()
        .filter_map(|(reference_id, value)| slots.get(&reference_id).map(|&slot| (slot, value)))
        .collect()
}

/// 建立订阅。`symbols` 中 None 表示调用方给的符号非法。
///
/// 返回每项的结果：`Ok(())` 已登记，`Err(SymbolNotFound)` 这个符号解析不到（只影响该项）。
/// 整体 `Err` 只在连接断开/超时或 PLC 拒绝整个订阅请求时发生。
pub fn create(
    connection: &mut Connection,
    resolver: &mut Resolver,
    cache: &mut SymbolCache,
    symbols: &[Option<&str>],
    cycle_time_ms: u16,
    now: Instant,
) -> Result<(ActiveSubscription, Vec<Result<(), IgwResultCode>>), IgwResultCode> {
    // 先把能解析的符号都解析出来(解析失败只影响该项)，再一次性发给 PLC
    let mut addresses: Vec<Option<ItemAddress>> = Vec::with_capacity(symbols.len());
    for symbol in symbols {
        match symbol {
            None => addresses.push(None),
            Some(symbol) => {
                let mut plc = reader::PlcSession { connection, resolver };
                addresses.push(reader::lookup_address(&mut plc, cache, symbol, now)?);
            }
        }
    }
    let (items, slots, results) = plan(|index, _| addresses[index].clone(), symbols);
    if items.is_empty() {
        return Err(IgwResultCode::SymbolNotFound);
    }

    let subscription = match connection.subscribe(&items, cycle_time_ms) {
        Ok(subscription) => subscription,
        Err(error) => return Err(crate::classify_operation(&error)),
    };
    let item_count = items.len();
    Ok((ActiveSubscription { subscription, slots, item_count }, results))
}

/// 取下一批通知值。返回 (调用方项序号, 值)。
///
/// PLC 每个周期都会给一个通知，没有变化时是空的（心跳），所以本调用在一个周期内就会返回，
/// 不会长时间占住连接锁。读超时不算故障：连接仍然可用，调用方下次再来即可。
pub fn poll(
    connection: &mut Connection,
    active: &mut ActiveSubscription,
) -> Result<Vec<(usize, PValue)>, IgwResultCode> {
    let notification = match connection.next_notification(&mut active.subscription) {
        Ok(notification) => notification,
        Err(error) if error.is_timeout() => return Ok(Vec::new()),
        Err(error) => return Err(crate::classify(&error)),
    };
    Ok(map_values(notification.values, &active.slots))
}

/// 删除订阅。连接已断时视为已删除——重连后 PLC 侧的订阅本来就没了。
pub fn delete(connection: &mut Connection, active: &ActiveSubscription) -> Result<(), IgwResultCode> {
    match connection.delete_subscription(&active.subscription) {
        Ok(()) => Ok(()),
        Err(error) if error.is_connection_lost() => Ok(()),
        Err(error) => Err(crate::classify_operation(&error)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn address(lid: u32) -> ItemAddress {
        ItemAddress { symbol_crc: 0, access_area: 1, access_sub_area: 2550, lid: vec![lid] }
    }

    /// 解析不到的符号只让它自己失败，其余照常订阅——和读取路径的逐项隔离一致。
    #[test]
    fn unresolved_symbols_only_fail_themselves() {
        let symbols = [Some("DB1.A"), Some("DB1.NoSuch"), None, Some("DB1.B")];
        let (items, slots, results) = plan(
            |index, _| if index == 1 { None } else { Some(address(index as u32)) },
            &symbols);

        assert_eq!(items.len(), 2, "只有解析到的两项发给 PLC");
        assert!(results[0].is_ok());
        assert_eq!(results[1], Err(IgwResultCode::SymbolNotFound));
        assert_eq!(results[2], Err(IgwResultCode::SymbolNotFound), "符号为空的项也只影响自己");
        assert!(results[3].is_ok());
        // 引用号必须能把值送回原来的项序号，哪怕中间有跳号
        assert_eq!(slots.get(&1), Some(&0));
        assert_eq!(slots.get(&4), Some(&3));
        assert_eq!(slots.get(&2), None);
    }

    /// 通知里的值按引用号对回项序号；不是我们订的引用号丢弃，不能错位。
    #[test]
    fn notification_values_map_back_by_reference_id() {
        let symbols = [Some("DB1.A"), Some("DB1.NoSuch"), Some("DB1.B")];
        let (_, slots, _) = plan(
            |index, _| if index == 1 { None } else { Some(address(index as u32)) },
            &symbols);

        let mapped = map_values(vec![
            (3, PValue::Int(7)),          // 第 3 项
            (1, PValue::Bool(true)),      // 第 1 项
            (99, PValue::Int(0)),         // 不认识：丢弃
        ], &slots);

        assert_eq!(mapped.len(), 2);
        assert_eq!(mapped[0].0, 2);
        assert_eq!(mapped[1].0, 0);
    }

    /// 一个都解析不到时不该向 PLC 发空订阅。
    #[test]
    fn nothing_resolvable_means_no_request() {
        let symbols = [Some("DB1.NoSuch"), None];
        let (items, _, results) = plan(|_, _| None, &symbols);

        assert!(items.is_empty());
        assert!(results.iter().all(|result| *result == Err(IgwResultCode::SymbolNotFound)));
    }
}
