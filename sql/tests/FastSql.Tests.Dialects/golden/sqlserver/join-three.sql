
SELECT [t0].[Id], [t1].[Name], [t2].[Sku] FROM [FastSqlOrder] AS [t0] INNER JOIN [FastSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] LEFT JOIN [FastSqlOrderItem] AS [t2] ON [t2].[OrderId] = [t0].[Id] WHERE [t1].[Name] IS NOT NULL
