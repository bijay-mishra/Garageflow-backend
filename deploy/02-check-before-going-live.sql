/* GarageFlow — pre-launch check. Reads only; changes nothing.

   Run this against the production database AFTER the API has started once
   (which is when it applies migrations and seeds), and BEFORE you give anyone
   the address.

   It exists because the app seeds a demo company and three accounts whose
   password is a constant in the source — DbSeeder.DemoPassword, "demo1234".
   That is right for a laptop and wrong for anything with a public hostname.
   The seeder only ever *adds*, so once you change a password it stays changed
   across restarts and deploys; the danger is purely that you never change it.

   Every row this returns with a NOT OK verdict is something to fix before
   launch. A clean run returns "OK" for all four checks. */

SET NOCOUNT ON;

PRINT '=== 1. Accounts still holding a seeded, publicly-known password ===';
PRINT '    Anyone who has read this repository can sign in as these.';
PRINT '    Fix: sign in, change the password, or delete the account.';

SELECT
    u.Email,
    u.Role,
    CASE WHEN u.CompanyCode = '' THEN '(platform operator)' ELSE u.CompanyCode END AS Company,
    u.IsActive,
    u.LastLoginAt,
    CASE
        WHEN u.LastLoginAt IS NULL THEN 'NOT OK - seeded and never signed into'
        ELSE 'CHECK - signed in; change the password if you have not'
    END AS Verdict
FROM Users u
WHERE u.Email IN (
    'bijaymishra276@gmail.com',      -- seeded owner AND the platform superadmin
    'mechanic@garageflow.demo',
    'customer@garageflow.demo'
);

PRINT '';
PRINT '=== 2. The superadmin account ===';
PRINT '    Belongs to no company and can read, suspend and delete every one of';
PRINT '    them. There should be exactly one, and it should not be reachable';
PRINT '    with a password that is in the source code.';

SELECT
    COUNT(*) AS SuperAdmins,
    CASE WHEN COUNT(*) = 1 THEN 'OK' ELSE 'NOT OK - expected exactly 1' END AS Verdict
FROM Users
WHERE Role = 'SuperAdmin';

PRINT '';
PRINT '=== 3. Demo data ===';
PRINT '    The DEMO company and its sample customers, vehicles and job cards.';
PRINT '    Harmless if you keep it deliberately; confusing if you forgot it is';
PRINT '    there and start entering real work beside it.';

SELECT
    'DEMO company' AS Item,
    COUNT(*) AS Rows_,
    CASE WHEN COUNT(*) = 0 THEN 'OK - not present' ELSE 'CHECK - delete it from the operator console if unwanted' END AS Verdict
FROM Workshops WHERE CompanyCode = 'DEMO'
UNION ALL
SELECT 'DEMO customers', COUNT(*),
    CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'CHECK - sample data' END
FROM Customers WHERE CompanyCode = 'DEMO'
UNION ALL
SELECT 'DEMO job cards', COUNT(*),
    CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'CHECK - sample data' END
FROM JobCards WHERE CompanyCode = 'DEMO';

PRINT '';
PRINT '=== 4. Tenant isolation ===';
PRINT '    Every tenant-owned row carries the company it belongs to, and the';
PRINT '    API filters on it globally. A blank code on one of these tables is a';
PRINT '    row no company owns and no query will ever return.';

SELECT 'Customers' AS TableName, COUNT(*) AS Orphaned FROM Customers WHERE CompanyCode = '' OR CompanyCode IS NULL
UNION ALL SELECT 'Vehicles',  COUNT(*) FROM Vehicles  WHERE CompanyCode = '' OR CompanyCode IS NULL
UNION ALL SELECT 'JobCards',  COUNT(*) FROM JobCards  WHERE CompanyCode = '' OR CompanyCode IS NULL
UNION ALL SELECT 'Invoices',  COUNT(*) FROM Invoices  WHERE CompanyCode = '' OR CompanyCode IS NULL
UNION ALL SELECT 'Payments',  COUNT(*) FROM Payments  WHERE CompanyCode = '' OR CompanyCode IS NULL
UNION ALL SELECT 'Workshops', COUNT(*) FROM Workshops WHERE CompanyCode = '' OR CompanyCode IS NULL;

PRINT '';
PRINT 'Anything above reading NOT OK is a launch blocker.';
PRINT 'Orphaned counts must all be 0. Users with a blank company code are';
PRINT 'expected and correct - that is how the platform operator is stored.';
