use igw_s7plus::{abi_version, IgwResultCode};

#[test]
fn abi_version_is_stable() {
    // v2：新增单项结果码 SymbolNotFound；托管侧据此把坏符号只标在该点上
    // v3：新增逐项写入 igw_s7plus_write_items 与结果码 ItemRejected
    // v4：新增变化订阅 igw_s7plus_subscribe / poll_notification / delete_subscription
    //     与结果码 SubscriptionNotFound；托管侧拿不到这几个导出就回退轮询
    assert_eq!(abi_version(), 5);
    assert_eq!(IgwResultCode::SymbolNotFound as i32, 34);
    assert_eq!(IgwResultCode::ItemRejected as i32, 35);
    assert_eq!(IgwResultCode::SubscriptionNotFound as i32, 36);
}

#[test]
fn result_codes_distinguish_authentication_from_transport_failures() {
    assert_ne!(IgwResultCode::AuthenticationRequired as i32, IgwResultCode::TransportFailure as i32);
    assert_ne!(IgwResultCode::AuthenticationFailed as i32, IgwResultCode::PermissionDenied as i32);
}
