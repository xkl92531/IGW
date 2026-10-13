# Siemens S7 Plus validation record

Automated protocol, ABI, managed transport, routing, credential-vault, API, UI,
publish, and portable-start tests do not replace a real PLC test. The following
hardware cases remain explicitly unverified until recorded by an operator.

| Case | Status | PLC / firmware | TIA | Date | Operator | Notes |
|---|---|---|---|---|---|---|
| S7-1200 optimized DB browse/read/write | 未验证 | — | — | — | — | Requires supported physical PLC or PLCSIM Advanced |
| S7-1200 mixed optimized/non-optimized DB | 未验证 | — | — | — | — | Verify simultaneous Plus and Classic routes |
| S7-1500 optimized DB browse/read/write | 未验证 | — | — | — | — | Include nested UDT and structure arrays |
| Anonymous full-access session | 未验证 | — | — | — | — | Confirm no credential prompt |
| Anonymous rejected, correct credential accepted | 未验证 | — | — | — | — | Confirm DPAPI persistence after Runtime restart |
| Wrong credential rejected | 未验证 | — | — | — | — | Confirm no Classic fallback for symbolic tags |
| Permission-limited account | 未验证 | — | — | — | — | Confirm detailed permission state |
| Project copied to a second machine | 未验证 | — | — | — | — | Project remains intact and prompts for re-entry |

When testing, record the exact PLC order number, firmware, configured protection
level, DB optimization flags, symbol count, scan period, read/write result, and
whether the project was downloaded again after access settings changed.
