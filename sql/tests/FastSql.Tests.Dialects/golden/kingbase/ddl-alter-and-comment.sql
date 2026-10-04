
ALTER TABLE "public"."FastSqlOrder" ADD COLUMN "Note" TEXT NULL ; ALTER TABLE "public"."FastSqlOrder" ALTER COLUMN "Amount" TYPE NUMERIC(18,4) ; ALTER TABLE "public"."FastSqlOrder" ALTER COLUMN "Amount" SET NOT NULL ; COMMENT ON COLUMN "public"."FastSqlOrder"."CustomerName" IS ''
