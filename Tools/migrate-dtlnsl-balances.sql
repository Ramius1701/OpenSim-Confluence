-- One-time migration: legacy DTLNSLMoneyModule/MoneyServer currency
-- data (balances/transactions tables) -> native ConfluenceCurrency
-- schema (currency_balances/currency_transactions).
--
-- Safe to run more than once (every INSERT is guarded against rows
-- that already exist in the target table) and safe to run against a
-- database that already has some native currency activity of its own
-- (Casperia-Dev has real balances from this session's own testing -
-- Jeffery Biedermann, Ramius Easterwood, ClaudeSecond Verify3 - this
-- script will never touch or overwrite those, it only inserts rows
-- for PrincipalIDs/TransactionIDs that don't already exist natively).
--
-- Run against Casperia-Dev's `casperia_dev` first to verify the
-- preview numbers look right, THEN against the live `casperia`
-- database as part of the actual production cutover - not before.
--
-- Usage:
--   mysql -h localhost -P 3306 -u casperia -pD7pibxuXXdOrk8sp <database> < migrate-dtlnsl-balances.sql

-- ============================================================
-- PREVIEW - run this section first and read the numbers before
-- running the INSERT section below. Nothing here writes anything.
-- ============================================================

-- Note: the legacy tables (balances/transactions) were created under
-- a different default collation (utf8mb3_uca1400_ai_ci) than the
-- native ones (utf8mb3_unicode_ci) - every cross-table UUID
-- comparison below needs an explicit COLLATE or MySQL/MariaDB refuses
-- the comparison outright ("Illegal mix of collations"). Safe to
-- force here since UUIDs are plain ASCII hex+hyphens - no accented
-- characters where the two collations could actually disagree.

SELECT 'Legacy balances table' AS what, COUNT(*) AS row_count, SUM(balance) AS total_balance FROM balances;
SELECT 'Legacy transactions table' AS what, COUNT(*) AS row_count FROM transactions;

SELECT 'Balances that will be INSERTED (no existing native row)' AS what, COUNT(*) AS row_count, SUM(balance) AS total_balance
FROM balances
WHERE user COLLATE utf8mb3_unicode_ci NOT IN (SELECT PrincipalID FROM currency_balances);

SELECT 'Balances that will be SKIPPED (native row already exists)' AS what, COUNT(*) AS row_count
FROM balances
WHERE user COLLATE utf8mb3_unicode_ci IN (SELECT PrincipalID FROM currency_balances);

SELECT 'Transactions that will be INSERTED (no existing native row)' AS what, COUNT(*) AS row_count
FROM transactions
WHERE UUID COLLATE utf8mb3_unicode_ci NOT IN (SELECT TransactionID FROM currency_transactions);

-- ============================================================
-- MIGRATE - only run past this point once the preview numbers
-- above have actually been reviewed.
-- ============================================================

-- Balances: straight UUID-to-UUID copy (both schemas key balances by
-- avatar PrincipalID directly - no identity mapping needed). Skips
-- any PrincipalID that already has a native currency_balances row,
-- so an avatar already active on the native system (e.g. from this
-- session's own Casperia-Dev testing) keeps their current native
-- balance untouched rather than being overwritten by their old,
-- possibly-stale legacy balance.
INSERT INTO currency_balances (PrincipalID, Balance)
SELECT user, balance
FROM balances
WHERE user COLLATE utf8mb3_unicode_ci NOT IN (SELECT PrincipalID FROM currency_balances);

-- Transactions: history carried over for audit/continuity. Column
-- notes:
--   - senderBalance/receiverBalance default to -1 in the legacy
--     schema when not recorded ("unknown") - GREATEST(x, 0) maps
--     that to 0 rather than migrating a nonsensical negative balance
--     into a field that's informational/point-in-time-display only
--     in the native schema (currency_balances is the only source of
--     truth for *current* balance either way).
--   - description is nullable/sometimes empty in the legacy schema;
--     falls back to commonName, then a generic label, since the
--     native schema's Description column is NOT NULL.
--   - TransferType is copied as the legacy system's own raw type
--     code, not remapped - migrated rows carry their original
--     system's taxonomy for historical accuracy; nothing in the
--     native system's own logic depends on interpreting a migrated
--     row's TransferType value, so this is safe.
INSERT INTO currency_transactions (TransactionID, ToAgent, FromAgent, Amount, TransferType, Description, Created, ToBalance, FromBalance)
SELECT
    UUID,
    receiver,
    sender,
    amount,
    type,
    COALESCE(NULLIF(description, ''), NULLIF(commonName, ''), 'Migrated from legacy currency system'),
    time,
    GREATEST(receiverBalance, 0),
    GREATEST(senderBalance, 0)
FROM transactions
WHERE UUID COLLATE utf8mb3_unicode_ci NOT IN (SELECT TransactionID FROM currency_transactions);

-- ============================================================
-- VERIFY - run after the INSERTs to confirm what actually landed.
-- ============================================================

SELECT 'Native currency_balances row count after migration' AS what, COUNT(*) AS row_count, SUM(Balance) AS total_balance FROM currency_balances;
SELECT 'Native currency_transactions row count after migration' AS what, COUNT(*) AS row_count FROM currency_transactions;
