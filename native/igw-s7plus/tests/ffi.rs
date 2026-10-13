use igw_s7plus::{
    IgwByteSlice, IgwConnectRequest, IgwMutableByteSlice, IgwReadItem, IgwReadResult,
    IgwResultCode, IgwValueType, IgwWriteItem,
    igw_s7plus_authenticate, igw_s7plus_browse, igw_s7plus_connect, igw_s7plus_probe,
    igw_s7plus_create, igw_s7plus_destroy, igw_s7plus_disconnect,
    igw_s7plus_read_batch, igw_s7plus_write_batch,
};

#[test]
fn handle_has_safe_create_destroy_lifecycle() {
    let handle = igw_s7plus_create();
    assert!(!handle.is_null());
    igw_s7plus_destroy(handle);
    igw_s7plus_destroy(std::ptr::null_mut());
}

#[test]
fn probe_requires_a_connected_session() {
    let handle = igw_s7plus_create();
    let result = igw_s7plus_probe(handle);
    assert_eq!(result.code, IgwResultCode::TransportFailure);
    igw_s7plus_destroy(handle);
}

#[test]
fn batch_operations_reject_a_missing_connection() {
    let handle = igw_s7plus_create();
    let symbol = IgwByteSlice::from_bytes(b"DB1.Value");
    let read_item = IgwReadItem { symbol };
    let mut read_result = IgwReadResult::default();
    let write_item = IgwWriteItem {
        symbol,
        value_type: IgwValueType::DInt,
        signed_value: 42,
        ..IgwWriteItem::default()
    };

    let read = igw_s7plus_read_batch(
        handle,
        &read_item,
        1,
        &mut read_result,
        IgwMutableByteSlice { ptr: std::ptr::null_mut(), len: 0 },
    );
    let write = igw_s7plus_write_batch(handle, &write_item, 1);

    assert_eq!(read.code, IgwResultCode::TransportFailure);
    assert_eq!(write.code, IgwResultCode::TransportFailure);
    igw_s7plus_destroy(handle);
}

#[test]
fn authenticated_operations_reject_a_missing_connection() {
    let handle = igw_s7plus_create();
    let user = IgwByteSlice::from_bytes(b"operator");
    let password = IgwByteSlice::from_bytes(b"secret");
    let mut output = [0u8; 32];

    let auth = igw_s7plus_authenticate(handle, user, password);
    let browse = igw_s7plus_browse(
        handle,
        IgwMutableByteSlice::from_bytes(&mut output),
    );

    assert_eq!(auth.code, IgwResultCode::TransportFailure);
    assert_eq!(browse.code, IgwResultCode::TransportFailure);
    assert_eq!(igw_s7plus_disconnect(handle).code, IgwResultCode::Ok);
    igw_s7plus_destroy(handle);
}

#[test]
fn byte_slices_reject_null_pointers_with_non_zero_lengths() {
    let handle = igw_s7plus_create();
    let invalid = IgwByteSlice {
        ptr: std::ptr::null(),
        len: 1,
    };

    let result = igw_s7plus_authenticate(handle, invalid, IgwByteSlice::empty());

    assert_eq!(result.code, IgwResultCode::InvalidArgument);
    igw_s7plus_destroy(handle);
}

#[test]
fn connect_rejects_missing_host_without_dereferencing_it() {
    let handle = igw_s7plus_create();
    let request = IgwConnectRequest {
        host_ptr: std::ptr::null(),
        host_len: 0,
        port: 102,
        timeout_ms: 2_000,
        legacy: 0,
    };

    let result = igw_s7plus_connect(handle, &request);

    assert_eq!(result.code, IgwResultCode::InvalidArgument);
    igw_s7plus_destroy(handle);
}
