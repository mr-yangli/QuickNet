namespace FastSql;

/// <summary>数据库种类。分派表由 G2 按这个枚举生成，因此成员顺序与名字都是契约。</summary>
public enum DbKind
{
    SqlServer = 0,
    MySql = 1,
    // Oracle 起往下 14 颗在 v0.37 第六笔各减 1：原本挂在 MySql 与 Oracle 之间、占 = 2 的 MariaDb
    //   于第五笔从枚举撤下，本笔把留下的那颗空洞压平（16 颗成员值域 0..15 连续）。
    //   压编号只改后备整数，不动成员顺序与名字，所以上面那句"顺序与名字都是契约"不被触碰；
    //   且分派走字符串 DriverKey、provider 与测试按名字取常量，仓库里没有一处读这颗整数（第五笔那条
    //   "留空洞不重编号"的判据据此改判，理由与设计文档 §14／尾注对齐）。
    Oracle = 2,
    PostgreSql = 3,
    Kingbase = 4,
    OpenGauss = 5,
    GaussDb = 6,
    Highgo = 7,
    Dameng = 8,
    ShenTong = 9,
    Xugu = 10,
    Gbase8s = 11,
    Gbase8c = 12,
    TiDb = 13,
    OceanBase = 14,
    Sqlite = 15,
}
