\pset pager off
\echo 'AEGIS / AETHER - standalone PostgreSQL demonstration'
SELECT current_database() AS database, current_user AS owner,
       inet_server_addr() AS address, inet_server_port() AS port;
\dt public.*
\echo 'Fresh original AETHER schema (no private accounts or message data):'
SELECT 'users' AS table_name, count(*) AS rows FROM users
UNION ALL SELECT 'crypto_devices', count(*) FROM crypto_devices
UNION ALL SELECT 'one_time_keys', count(*) FROM one_time_keys
UNION ALL SELECT 'messages', count(*) FROM messages
UNION ALL SELECT 'message_receipts', count(*) FROM message_receipts;
\echo 'Accounts created in this database:'
SELECT username, account_no, created_at FROM users ORDER BY account_no;
\echo 'Table structure, primary keys and indexes:'
\d users
\d messages
