//! 符号解析器：把 `DB名.成员[下标].成员` 解析为 S7CommPlus 的 ItemAddress。
//!
//! 逻辑移植自 s7commplus 0.1.0 的 `Connection::resolve_full`、`parse_level`、`array_element_id`
//! (s7commplus: LGPL-3.0-or-later AND MIT；其 MIT 部分源自 HarpoS7，版权归原作者)。
//! 与原实现的区别只在性能与两处边界修正：
//! - 原实现每解析一个符号都复制一次整个 DB 列表，并在每一层复制一次类型对象、线性查找成员名；
//!   这里 DB 列表只取一次建成哈希表，每个类型只取一次并建成“成员名 → 下标”的哈希表(35 万点冷启动从十几秒降到亚秒级)。
//! - 原实现 M 区类型取不到时直接报错、不再尝试 Q/I 区；这里跳过取不到的区继续尝试。
//! - 原实现数组下标越界检查用 `>`(接受越界一位的下标)；这里用 `>=`，越界下标按找不到处理。
//!
//! 类型与 DB 列表与连接同寿命：连接重建(含程序变更自愈)时整体丢弃重来。

use std::collections::HashMap;
use std::rc::Rc;

use s7commplus::DataBlock;
use s7commplus::proto::{ItemAddress, OffsetInfo, PObject, VartypeElement};
use s7commplus::{Connection, Error};

/// 数据块读写的 AccessSubArea(`Ids.DB_ValueActual`)。
const DB_VALUE_ACTUAL: u32 = 2550;
/// I/Q/M 区读写的 AccessSubArea(`Ids.ControllerArea_ValueActual`)。
const CONTROLLER_AREA_VALUE_ACTUAL: u32 = 3736;
/// 可按符号访问的控制器区：(AccessArea RID, 类型 relid)，按参考实现的顺序 M、Q、I。
const CONTROLLER_AREAS: [(u32, u32); 3] = [(82, 0x9003_0000), (81, 0x9002_0000), (80, 0x9001_0000)];

/// 解析所需的 PLC 元数据来源(便于脱离 PLC 测试)。
pub trait TypeSource {
    fn datablocks(&mut self) -> Result<Vec<DataBlock>, Error>;
    fn type_info(&mut self, relid: u32) -> Result<PObject, Error>;
}

impl TypeSource for Connection {
    fn datablocks(&mut self) -> Result<Vec<DataBlock>, Error> { self.datablock_list() }
    fn type_info(&mut self, relid: u32) -> Result<PObject, Error> { Connection::type_info(self, relid) }
}

/// 一个类型的成员表：名字 → 成员(与原实现一样，同名取第一个)。
struct TypeIndex {
    members: HashMap<String, VartypeElement>,
}

impl TypeIndex {
    fn from(object: PObject) -> Result<TypeIndex, String> {
        let names = object.varname_list.ok_or("type info missing VarnameList")?;
        let types = object.vartype_list.ok_or("type info missing VartypeList")?;
        let mut members = HashMap::with_capacity(names.names.len());
        for (index, name) in names.names.into_iter().enumerate() {
            let element = types.elements.get(index).ok_or("VartypeList shorter than VarnameList")?;
            members.entry(name).or_insert_with(|| element.clone());
        }
        Ok(TypeIndex { members })
    }
}

/// 解析失败：`Plc` 为连接断开/超时等(调用方整批上抛)，`NotFound` 为符号在程序中找不到。
#[derive(Debug)]
pub enum ResolveError {
    Plc(Error),
    NotFound(String),
}

#[derive(Default)]
pub struct Resolver {
    /// DB 名 → (对象 relid, 类型 relid)。同名取第一个。
    dbs: Option<HashMap<String, (u32, u32)>>,
    /// 类型 relid → 成员表；取不到成员表的类型(如受保护块)记为 Err，不再重复向 PLC 请求。
    types: HashMap<u32, Result<Rc<TypeIndex>, String>>,
}

impl Resolver {
    pub fn resolve<S: TypeSource>(&mut self, source: &mut S, symbol: &str) -> Result<ItemAddress, ResolveError> {
        let levels: Vec<&str> = symbol.split('.').collect();
        let first = parse_level(levels.first().copied().unwrap_or("")).0;

        if self.dbs.is_none() {
            let list = source.datablocks().map_err(ResolveError::Plc)?;
            let mut map = HashMap::with_capacity(list.len());
            for db in list {
                map.entry(db.name).or_insert((db.relid, db.ti_relid));
            }
            self.dbs = Some(map);
        }
        // 数据块占掉第一段(DB 名)；控制器区(M/Q/I)不占，第一段就是区里的变量名。
        let (access_area, access_sub_area, root_ti, start) =
            if let Some(&(relid, ti)) = self.dbs.as_ref().and_then(|dbs| dbs.get(first)) {
                (relid, DB_VALUE_ACTUAL, ti, 1usize)
            } else {
                let mut found = None;
                for (rid, ti) in CONTROLLER_AREAS {
                    let Ok(area) = self.type_index(source, ti)? else { continue };
                    if area.members.contains_key(first) {
                        found = Some((rid, CONTROLLER_AREA_VALUE_ACTUAL, ti, 0usize));
                        break;
                    }
                }
                found.ok_or_else(|| ResolveError::NotFound(format!("symbol '{symbol}' not found in any data block or M/Q/I area")))?
            };

        let mut address = ItemAddress { symbol_crc: 0, access_area, access_sub_area, lid: Vec::new() };
        let mut ti_relid = root_ti;
        let mut i = start;
        while i < levels.len() {
            let (name, indices) = parse_level(levels[i]);
            let ty = self.type_index(source, ti_relid)?.map_err(ResolveError::NotFound)?;
            let element = ty.members.get(name)
                .ok_or_else(|| ResolveError::NotFound(format!("member '{name}' not found")))?;
            let oi = &element.offset_info;
            address.lid.push(element.lid);
            // 数组下标：追加从 0 开始的行优先元素号；元素是结构体时再追加 .1
            if !indices.is_empty() {
                let array_lid = array_element_id(oi, &indices)
                    .ok_or_else(|| ResolveError::NotFound(format!("bad array index for '{name}'")))?;
                address.lid.push(array_lid);
                if oi.has_relation() {
                    address.lid.push(1);
                }
            } else if (oi.is_1dim || oi.is_mdim) && oi.has_relation() {
                return Err(ResolveError::NotFound(format!("whole array-of-struct read of '{name}' not supported; index it")));
            }
            let relation = oi.relation_id;
            i += 1;
            match relation {
                Some(rel) if i < levels.len() => ti_relid = rel,
                _ => break,
            }
        }
        if i < levels.len() {
            return Err(ResolveError::NotFound(format!("could not fully resolve '{symbol}' (stopped before '{}')", levels[i])));
        }
        Ok(address)
    }

    /// 取类型成员表(每个类型只向 PLC 取一次)。外层 Err = 连接类错误；内层 Err = 该类型没有成员表。
    fn type_index<S: TypeSource>(&mut self, source: &mut S, relid: u32) -> Result<Result<Rc<TypeIndex>, String>, ResolveError> {
        if let Some(cached) = self.types.get(&relid) {
            return Ok(cached.clone());
        }
        let entry = match source.type_info(relid) {
            Ok(object) => TypeIndex::from(object).map(Rc::new),
            Err(error) if error.is_connection_lost() || error.is_timeout() => return Err(ResolveError::Plc(error)),
            Err(error) => Err(error.to_string()),
        };
        self.types.insert(relid, entry.clone());
        Ok(entry)
    }
}

/// `arr[2]` → ("arr", [2])，`m[1,2]` → ("m", [1, 2])，`field` → ("field", [])。
///
/// TIA 允许成员名里带方括号(KUKA 接口 DB 的 `[O17]PRO_MOVE`、`[O1]PGNOFBK` 就是这样)，
/// 所以只有「名字[整数,整数…]」这种方括号在名字后面、以 `]` 收尾、下标全是整数的写法才算数组访问；
/// 其余一律当成成员名的一部分原样查找，否则这些点会因为名字被截断而永远解析不到。
fn parse_level(level: &str) -> (&str, Vec<i32>) {
    let Some(open) = level.find('[') else { return (level, Vec::new()) };
    if open == 0 || !level.ends_with(']') {
        return (level, Vec::new());
    }
    let inner = &level[open + 1..level.len() - 1];
    if inner.is_empty() {
        return (level, Vec::new());
    }
    let mut indices = Vec::with_capacity(1);
    for part in inner.split(',') {
        match part.trim().parse::<i32>() {
            Ok(index) => indices.push(index),
            Err(_) => return (level, Vec::new()),
        }
    }
    (&level[..open], indices)
}

/// 数组访问的元素号(从 0 开始、行优先)。维数或下标越界时返回 None。
#[allow(clippy::needless_range_loop)]
fn array_element_id(oi: &OffsetInfo, indices: &[i32]) -> Option<u32> {
    if oi.is_1dim {
        if indices.len() != 1 {
            return None;
        }
        let zero = indices[0].checked_sub(oi.array_lower_bounds)?;
        if zero < 0 || (zero as u32) >= oi.array_element_count {
            return None;
        }
        Some(zero as u32)
    } else if oi.is_mdim {
        let dim_count = oi.mdim_element_count.iter().filter(|&&c| c > 0).count();
        if dim_count == 0 || dim_count != indices.len() {
            return None;
        }
        let mut idx = vec![0i64; dim_count];
        for i in 0..dim_count {
            let lb = oi.mdim_lower_bounds[dim_count - i - 1];
            let v = indices[i].checked_sub(lb)?;
            if v < 0 || (v as u32) >= oi.mdim_element_count[dim_count - i - 1] {
                return None;
            }
            idx[i] = v as i64;
        }
        let mut dim_size = vec![1u64; dim_count];
        let mut g = 1u64;
        for i in 0..dim_count - 1 {
            dim_size[i] = g;
            g *= oi.mdim_element_count[i] as u64;
        }
        dim_size[dim_count - 1] = g;
        let mut array_index = 0i64;
        for i in 0..dim_count {
            array_index += idx[i] * dim_size[dim_count - i - 1] as i64;
        }
        u32::try_from(array_index).ok()
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use s7commplus::proto::{VarnameList, VartypeList};

    fn element(lid: u32, relation: Option<u32>, dims: Option<(i32, u32)>) -> VartypeElement {
        let mut offset_info = OffsetInfo { relation_id: relation, ..Default::default() };
        if let Some((lower, count)) = dims {
            offset_info.is_1dim = true;
            offset_info.array_lower_bounds = lower;
            offset_info.array_element_count = count;
        }
        VartypeElement { lid, symbol_crc: 0, softdatatype: 0, attribute_flags: 0, bitoffsetinfo_flags: 0, offset_info }
    }

    fn type_object(members: &[(&str, VartypeElement)]) -> PObject {
        PObject {
            varname_list: Some(VarnameList { names: members.iter().map(|(name, _)| (*name).to_owned()).collect() }),
            vartype_list: Some(VartypeList { first_id: 1, elements: members.iter().map(|(_, e)| e.clone()).collect() }),
            ..Default::default()
        }
    }

    #[derive(Default)]
    struct FakeTypes {
        types: HashMap<u32, PObject>,
        dbs: Vec<DataBlock>,
        datablock_calls: usize,
        type_calls: usize,
    }

    impl TypeSource for FakeTypes {
        fn datablocks(&mut self) -> Result<Vec<DataBlock>, Error> {
            self.datablock_calls += 1;
            Ok(self.dbs.clone())
        }
        fn type_info(&mut self, relid: u32) -> Result<PObject, Error> {
            self.type_calls += 1;
            self.types.get(&relid).cloned()
                .ok_or_else(|| Error::Protocol(format!("type info for relid {relid} carries no member list")))
        }
    }

    fn plc() -> FakeTypes {
        let mut fake = FakeTypes::default();
        fake.dbs = vec![DataBlock { name: "DB1".into(), relid: 0x8a0e_0001, number: 1, ti_relid: 100 }];
        // DB1 { A: Int; S: Struct{ X: Int }; Arr: Array[1..3] of Int; SA: Array[0..1] of Struct{ Y: Int } }
        fake.types.insert(100, type_object(&[
            ("A", element(2, None, None)),
            ("S", element(3, Some(200), None)),
            ("Arr", element(4, None, Some((1, 3)))),
            ("SA", element(5, Some(300), Some((0, 2)))),
            // TIA 允许的带方括号成员名(现场 KUKA 接口 DB："[O17]PRO_MOVE"、"[O1]PGNOFBK")
            ("[O17]PRO_MOVE", element(13, None, None)),
            ("1#CreateCamDone", element(14, None, None)),
            ("NoCellData1&2", element(15, None, None)),
        ]));
        fake.types.insert(200, type_object(&[("X", element(7, None, None))]));
        fake.types.insert(300, type_object(&[("Y", element(9, None, None))]));
        // M 区取不到成员表，Q 区有 QFlag
        fake.types.insert(0x9002_0000, type_object(&[("QFlag", element(11, None, None))]));
        fake
    }

    fn lids(resolver: &mut Resolver, fake: &mut FakeTypes, symbol: &str) -> Option<(u32, u32, Vec<u32>)> {
        resolver.resolve(fake, symbol).ok().map(|a| (a.access_area, a.access_sub_area, a.lid))
    }

    #[test]
    fn resolves_members_structs_arrays_and_arrays_of_structs() {
        let (mut fake, mut resolver) = (plc(), Resolver::default());
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.A"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![2])));
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.S.X"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![3, 7])));
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.Arr[3]"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![4, 2])));
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.SA[1].Y"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![5, 1, 1, 9])));
    }

    /// 成员名里可以带方括号、井号、和号：这些不是数组下标，名字要原样查找。
    /// 现场 KUKA 接口 DB 的 "[O17]PRO_MOVE" 曾因名字被截成空串而永远解析不到，
    /// 在逐项隔离之前还会把同一批其它点一起拖成坏值。
    #[test]
    fn member_names_with_brackets_hashes_and_ampersands_resolve() {
        let (mut fake, mut resolver) = (plc(), Resolver::default());
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.[O17]PRO_MOVE"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![13])));
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.1#CreateCamDone"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![14])));
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.NoCellData1&2"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![15])));
        // 真正的数组访问不受影响
        assert_eq!(lids(&mut resolver, &mut fake, "DB1.Arr[3]"), Some((0x8a0e_0001, DB_VALUE_ACTUAL, vec![4, 2])));
        // 方括号在名字后面但内容不是整数：当成员名处理，不当下标
        assert!(matches!(resolver.resolve(&mut fake, "DB1.Arr[x]"), Err(ResolveError::NotFound(_))));
    }

    #[test]
    fn parse_level_only_treats_trailing_integer_brackets_as_indices() {
        assert_eq!(parse_level("Arr[3]"), ("Arr", vec![3]));
        assert_eq!(parse_level("M[1,2]"), ("M", vec![1, 2]));
        assert_eq!(parse_level("[O17]PRO_MOVE"), ("[O17]PRO_MOVE", vec![]));
        assert_eq!(parse_level("Name[abc]"), ("Name[abc]", vec![]));
        assert_eq!(parse_level("Name[]"), ("Name[]", vec![]));
        assert_eq!(parse_level("Unclosed[3"), ("Unclosed[3", vec![]));
        assert_eq!(parse_level("Plain"), ("Plain", vec![]));
    }

    #[test]
    fn each_type_and_the_db_list_are_fetched_once() {
        let (mut fake, mut resolver) = (plc(), Resolver::default());
        for _ in 0..1000 {
            lids(&mut resolver, &mut fake, "DB1.S.X");
            lids(&mut resolver, &mut fake, "DB1.A");
        }
        assert_eq!(fake.datablock_calls, 1);
        assert_eq!(fake.type_calls, 2, "DB1 类型与 S 的结构类型各取一次");
    }

    #[test]
    fn controller_areas_skip_an_area_without_member_list() {
        let (mut fake, mut resolver) = (plc(), Resolver::default());
        assert_eq!(lids(&mut resolver, &mut fake, "QFlag"), Some((81, CONTROLLER_AREA_VALUE_ACTUAL, vec![11])));
        let calls = fake.type_calls;
        assert!(resolver.resolve(&mut fake, "Nothing").is_err());
        assert_eq!(fake.type_calls, calls + 1, "M 区的失败已缓存，只再取一次 I 区");
    }

    #[test]
    fn out_of_range_and_unknown_members_are_not_found() {
        let (mut fake, mut resolver) = (plc(), Resolver::default());
        assert!(matches!(resolver.resolve(&mut fake, "DB1.Arr[4]"), Err(ResolveError::NotFound(_))), "越界一位也拒绝");
        assert!(matches!(resolver.resolve(&mut fake, "DB1.Arr[0]"), Err(ResolveError::NotFound(_))));
        assert!(matches!(resolver.resolve(&mut fake, "DB1.Nope"), Err(ResolveError::NotFound(_))));
        assert!(matches!(resolver.resolve(&mut fake, "DB1.SA.Y"), Err(ResolveError::NotFound(_))));
        assert!(matches!(resolver.resolve(&mut fake, "DB1.A.More"), Err(ResolveError::NotFound(_))));
    }

    #[test]
    fn multi_dimensional_index_is_row_major() {
        let mut oi = OffsetInfo { is_mdim: true, ..Default::default() };
        oi.mdim_element_count[0] = 3;   // 最快变化的维
        oi.mdim_element_count[1] = 2;
        assert_eq!(array_element_id(&oi, &[0, 0]), Some(0));
        assert_eq!(array_element_id(&oi, &[0, 2]), Some(2));
        assert_eq!(array_element_id(&oi, &[1, 0]), Some(3));
        assert_eq!(array_element_id(&oi, &[1, 3]), None);
    }
}
