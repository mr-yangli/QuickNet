
SELECT [o].[Id], [o].[CustomerName], [o].[CustomerId], [o].[Amount], [o].[State], [o].[CreatedAt], [o].[PaidAt], [o].[Note] FROM [FastSqlOrder] AS [o] ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT @p0 ROWS ONLY
