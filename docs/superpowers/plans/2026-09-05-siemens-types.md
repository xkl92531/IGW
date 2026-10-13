# Siemens OPC UA data types

User authorization: add DTL, then extend this to the Siemens documented data types. Continue implementation and package 1.6.34 without interrupting the user for routine approval.

## Global constraints

- Preserve existing uncommitted features and enum values 0..16. Do not commit or reset the working tree.
- Target the onsite S7-1500 OPC UA path. Use the server DataType contract; do not guess a date type from a byte array's length.
- Cover the complete basic type table documented for S7-1500 OPC UA, with arrays and existing STRUCT/UDT browsing. PLC-only pointers and block handles that the server cannot expose must not be advertised as readable values.
- Preserve all 9 nanosecond digits in DTL; no local timezone conversion of Siemens calendar values. Reject invalid dates and lossy writes.
- DTL writes use one complete ExtensionObject with the original binary encoding TypeId. PLC memory byte order and OPC UA binary byte order are different.
- Keep type metadata reads batched and cached; no extra fresh Value reads for ordinary scalars.
- Retain access permissions, array bounds, and source datatype metadata through import.
- Run regression tests, frontend typecheck/build, publish and verify installer 1.6.34. Real onsite PLC is inaccessible; describe local validation accurately.

## Sources

- https://docs.tia.siemens.cloud/r/simatic_et_200eco_pn_manual_collection_itit_20/function-manuals/communication-function-manuals/communication/opc-ua-communication/using-the-s7-1500-as-an-opc-ua-server/interesting-information-about-the-opc-ua-server-of-the-s7-1500-cpus/mapping-of-data-types
- https://docs.tia.siemens.cloud/r/en-us/v20/data-types/date-and-time/dtl-s7-1200-s7-1500

## Shared interfaces

Append IgwDataType values: Dtl=17, Date=18, Tod=19, Ltod=20, Time=21, Ltime=22, Dt=23, Ldt=24, S5Time=25, S7Char=26, WChar=27, Word=28, DWord=29, LWord=30, Timer=31, Counter=32, SInt=33, DateTime=34. Generic types stay compatible. S7 CHAR is a byte character, distinct from legacy KEP Char (signed byte).

IGW.Abstractions.SiemensValueCodec provides:
- bool IsSemantic(IgwDataType type), bool IsText(IgwDataType type)
- IgwDataType? FromTypeName(string name)
- object Normalize(IgwDataType type, object value): decode wire primitive / validate user text into a canonical runtime value.
- object ToWire(IgwDataType type, object value): canonical input to the OPC UA primitive (DT byte[8], LDT DateTime, etc.). DTL handled by the protocol adapter.
- string DecodeDtl(ReadOnlySpan<byte> bytes, bool littleEndian=false)
- byte[] EncodeDtl(object value, bool littleEndian=false)

Text runtime values: DTL/LDT yyyy-MM-dd HH:mm:ss.fffffffff; DT yyyy-MM-dd HH:mm:ss.fff; DATE yyyy-MM-dd; TOD HH:mm:ss.fff; LTOD HH:mm:ss.fffffffff; TIME T#<signed integer>ms; LTIME LT#<signed integer>ns; S5TIME S5T#<integer>ms; S7Char/WChar one character. DateTime uses invariant roundtrip UTC text. Numeric input is accepted in native wire units for time types. Writes may use Siemens prefixes or canonical text. Word/DWord/LWord/Timer/Counter/SInt use exact integer CLR widths. DTL accepts raw 12-byte PLC memory explicitly, never by guessing from an untyped value.

## Tasks

1. Foundation (root): test-first codec and enum; normalize writes; REST input, array JSON codec, northbound representations, MES compatibility; accurate errors.
2. Frontend (UI implementer): expose catalog in point editor/import/display/write, distinguish legacy Char, preserve large integers as text on write, add practical input help and tests. See frontend brief.
3. OPC UA (protocol implementer): recognize Siemens DataType BrowseNames in batched metadata reads; DTL scalar-with-children import, polling/subscriptions/write conversion with original representation; semantic arrays and tests. See OPC UA brief.
4. Integration (root): inspect diffs and regressions, fill gaps in structures/type handling, verify documentation matrix. Independent final review after implementation.
5. Release (root): test, typecheck/build/publish, bump once, create installer 1.6.34, smoke with isolated data and release notes.

## Completed
All tasks completed. Backend 781/781; frontend full 136 and final race regression 11 passed; build/publish and installer compile passed. Isolated published runtime health and browser logs verified. Package: installer/out/IGW-Setup-1.6.34.exe. Detailed boundaries and formats: docs/siemens-opcua-types.md. Review findings resolved in artifacts/siemens-types/review.md. No onsite PLC access or installer overwrite performed.
