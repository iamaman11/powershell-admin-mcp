# Windows TCP MCP catalog compatibility

The native service already supports `SystemQueryKind.TCP_CONNECTIONS` in
`windows.system_query`, but the client-side tool manifest can outlive the server
release. A connector that still advertises only six `kind` values cannot
express `TCP_CONNECTIONS`, even when an upgraded server accepts it.

The server therefore advertises **two** read-only, bounded, typed entry points:

- `windows.system_query(kind="TCP_CONNECTIONS", pid?, page_size?, offset?)`, kept
  for clients whose generated enum is current.
- `windows.tcp_connections(pid?, page_size?, offset?)`, with no enum discriminator
  in its input schema. It reuses the same `GetExtendedTcpTable` snapshot, but
  publishes a dedicated `TcpConnectionsResult` output schema with **only**
  `status`, `kind: "TCP_CONNECTIONS"`, `observed_utc`, `tcp_connections`,
  `next_offset`, `native_error`, and `elapsed_ms`. Unrelated processes,
  services, events, registry, and task schemas are not exposed by this tool.

An MCP `tools/list` regression test (`tests/Test-TcpCatalog.ps1 -Exe <path>`) checks
the actual freshly generated server schema, including the **exact** dedicated
TCP output fields, and rejects missing tool definitions or output schema bloat.
It does **not** test or forcibly refresh the externally published connector
catalog. That catalog must be refreshed/discovered by the connector operator
after the new build is installed. Until the new tool is visible to the client,
mark client coverage **NOT_TESTED**, not `COMPLETED`.

Once the connector advertises the new tool, use a harmless read-only query
(`page_size: 1`), compare status and result with `windows.system_query` when
the updated enum is available, and verify that both return typed results.
Neither entry point opens outbound sockets, alters network state, nor invokes
PowerShell. Missing PID connections are `NOT_FOUND`; a process with no entries
is not by itself proof the process does not exist.
