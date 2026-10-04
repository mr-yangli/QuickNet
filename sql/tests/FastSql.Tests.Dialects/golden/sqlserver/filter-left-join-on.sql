
SELECT [t0].[Id], [t1].[Title] FROM [FastSqlOrder] AS [t0] LEFT JOIN [FastSqlTicket] AS [t1] ON [t0].[Id] = [t1].[TenantId] AND NOT [t1].[Deleted] = 1 WHERE [t0].[Amount] > @p0
