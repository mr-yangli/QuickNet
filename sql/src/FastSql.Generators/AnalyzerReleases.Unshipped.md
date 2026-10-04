### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FastSql001 | FastSql.Metadata | Error | 同一层元数据来源互斥，对应反射侧的 MetadataConflictException（§10.2）
FastSql002 | FastSql.Metadata | Error | 索引引用了未映射的属性，对应 ContractResolver 的同一句失败
FastSql003 | FastSql.Metadata | Info | 该实体不走 G1，运行期退回 G2 反射解析（§12.7 A）
FastSql004 | FastSql.Metadata | Error | [ForeignKey] 指向了未映射的属性，对应 EntityContractFactory 收尾时的那句启动期失败
