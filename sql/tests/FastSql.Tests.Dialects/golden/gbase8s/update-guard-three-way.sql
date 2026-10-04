
UPDATE "FastSqlOrder" SET "Amount" = ? WHERE "FastSqlOrder"."PaidAt" <> "t1"."PaidAt" OR ("FastSqlOrder"."PaidAt" IS NULL AND "t1"."PaidAt" IS NOT NULL) OR ("FastSqlOrder"."PaidAt" IS NOT NULL AND "t1"."PaidAt" IS NULL)
