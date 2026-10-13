CREATE TABLE project_meta(
    singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
    project_id TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NULL,
    schema_version INTEGER NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    runtime_generation INTEGER NOT NULL DEFAULT 0,
    legacy_source_hash TEXT NULL,
    migration_completed_at TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE channels(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    driver_id TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
    settings_json TEXT NOT NULL DEFAULT '{}',
    igw_properties_json TEXT NOT NULL DEFAULT '{}',
    kep_properties_json TEXT NOT NULL DEFAULT '{}',
    sort_order INTEGER NOT NULL DEFAULT 0,
    row_version INTEGER NOT NULL DEFAULT 1,
    UNIQUE(name)
);

CREATE TABLE devices(
    id TEXT PRIMARY KEY,
    channel_id TEXT NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    endpoint TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
    template_ref TEXT NULL,
    delta_json TEXT NULL,
    settings_json TEXT NOT NULL DEFAULT '{}',
    igw_properties_json TEXT NOT NULL DEFAULT '{}',
    kep_properties_json TEXT NOT NULL DEFAULT '{}',
    sort_order INTEGER NOT NULL DEFAULT 0,
    row_version INTEGER NOT NULL DEFAULT 1,
    UNIQUE(channel_id, name)
);

CREATE TABLE tag_groups(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    parent_group_id TEXT NULL REFERENCES tag_groups(id) ON DELETE CASCADE,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    canonical_path TEXT NOT NULL,
    flow_enabled INTEGER NOT NULL DEFAULT 0 CHECK(flow_enabled IN (0, 1)),
    igw_properties_json TEXT NOT NULL DEFAULT '{}',
    kep_properties_json TEXT NOT NULL DEFAULT '{}',
    sort_order INTEGER NOT NULL DEFAULT 0,
    row_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE tags(
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    group_id TEXT NULL REFERENCES tag_groups(id) ON DELETE CASCADE,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    canonical_path TEXT NOT NULL,
    address TEXT NOT NULL DEFAULT '',
    data_type INTEGER NOT NULL,
    access INTEGER NOT NULL,
    scan_rate_ms INTEGER NOT NULL,
    string_length INTEGER NULL,
    text_encoding INTEGER NULL,
    eng_unit TEXT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    row_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE tag_options(
    tag_id TEXT PRIMARY KEY REFERENCES tags(id) ON DELETE CASCADE,
    is_array INTEGER NOT NULL DEFAULT 0 CHECK(is_array IN (0, 1)),
    array_json TEXT NULL,
    scaling_json TEXT NULL,
    deadband REAL NOT NULL DEFAULT 0,
    deadband_type INTEGER NOT NULL DEFAULT 0,
    alarm_json TEXT NULL,
    write_confirmation_json TEXT NULL,
    metadata_json TEXT NULL,
    igw_properties_json TEXT NOT NULL DEFAULT '{}',
    kep_properties_json TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE project_sections(
    section_key TEXT PRIMARY KEY,
    schema_version INTEGER NOT NULL,
    document_json TEXT NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE mes_tasks(
    id TEXT PRIMARY KEY,
    group_id TEXT NULL REFERENCES tag_groups(id) ON DELETE SET NULL,
    group_ref TEXT NULL,
    device_ref TEXT NULL,
    enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN (0, 1)),
    flow_available INTEGER NOT NULL DEFAULT 1 CHECK(flow_available IN (0, 1)),
    name TEXT NOT NULL,
    template_id TEXT NULL,
    flow_document_json TEXT NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE mes_task_index(
    task_id TEXT NOT NULL REFERENCES mes_tasks(id) ON DELETE CASCADE,
    node_type TEXT NOT NULL,
    referenced_tag_id TEXT NULL REFERENCES tags(id) ON DELETE SET NULL,
    referenced_path TEXT NULL,
    PRIMARY KEY(task_id, node_type, referenced_tag_id, referenced_path)
);

CREATE TABLE flow_templates(
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    template_document_json TEXT NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 1,
    UNIQUE(name)
);

CREATE TABLE device_templates(
    id TEXT PRIMARY KEY,
    driver_id TEXT NOT NULL,
    name TEXT NOT NULL COLLATE NOCASE,
    description TEXT NULL,
    template_document_json TEXT NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 1,
    UNIQUE(name)
);

CREATE TABLE secret_entries(
    owner_type TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    secret_name TEXT NOT NULL,
    ciphertext TEXT NOT NULL,
    nonce TEXT NULL,
    authentication_tag TEXT NULL,
    key_version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(owner_type, owner_id, secret_name)
);

CREATE UNIQUE INDEX ux_groups_root_name
    ON tag_groups(device_id, name) WHERE parent_group_id IS NULL;
CREATE UNIQUE INDEX ux_groups_child_name
    ON tag_groups(device_id, parent_group_id, name) WHERE parent_group_id IS NOT NULL;
CREATE UNIQUE INDEX ux_tags_root_name
    ON tags(device_id, name) WHERE group_id IS NULL;
CREATE UNIQUE INDEX ux_tags_group_name
    ON tags(device_id, group_id, name) WHERE group_id IS NOT NULL;
CREATE INDEX ix_groups_parent_order ON tag_groups(device_id, parent_group_id, sort_order);
CREATE INDEX ix_tags_group_order ON tags(device_id, group_id, sort_order);
CREATE INDEX ix_tags_path ON tags(canonical_path);
CREATE INDEX ix_tags_address ON tags(address);
CREATE INDEX ix_mes_tasks_group ON mes_tasks(group_id);
CREATE INDEX ix_mes_task_refs ON mes_task_index(referenced_tag_id, referenced_path);
