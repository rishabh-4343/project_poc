# Auditor schema

Two stores. PostgreSQL holds configuration and app state; ClickHouse holds the
high-volume audit facts. Each store is split per module so three developers can
work without clashing.

## Layout
```
db/                      PostgreSQL (one SCHEMA per module)
  core/    0001_core.sql      core.*   shared; platform-owned, reviewed
  audit/   0001_audit.sql     audit.*  auditing module
           0002_ddc_contract.sql  (ddc.* — really owned by the DDC dev; here for the demo)
  ddc/     ...                ddc.*    DDC module          (another dev)
  permissions/ ...           permissions.*                (another dev)
ch/                      ClickHouse (one DATABASE per module)
  audit/   0001_audit.sql     audit.actions, audit.read_actions, rollups
  ddc/     ...                ddc.*
```

## Ownership rules (prevent clashes)
1. One PostgreSQL **schema** and one ClickHouse **database** per module; one owner each.
2. A module writes only its own schema/database. It may **read** `core`; it never
   writes or alters `core`.
3. No cross-module joins or writes. If module A needs module B's data, it goes
   through B's backend API.
4. `core` changes are reviewed by the platform owner; modules declare a minimum
   core version.

## Migrations
Each module has its own ordered files and its own tracking table
(`core.schema_migrations`, `audit.schema_migrations`, ...). Run with DbUp or Flyway,
pointed at one module folder at a time. `core` runs first. Because each module only
touches its own objects and its own tracking table, parallel migrations never collide.

Apply order on a fresh DB:
  PostgreSQL:  db/core/*  then  each module's db/<m>/*
  ClickHouse:  ch/<m>/*

## Identity of an audit action
There is no Windows Security-log record number (the driver is the source), so an
action is identified by **(tenant_id, server_id, seq)** where `seq` is a monotonic
counter the agent assigns per server. ReplacingMergeTree collapses a resent batch.
