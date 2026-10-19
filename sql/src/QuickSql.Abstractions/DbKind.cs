namespace QuickSql;

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
    // 第 17 颗（本笔：DuckDB 落笔）。追加在末位、前 16 颗稳定值一颗不动（上面那句"顺序与名字都是契约"
    //   不被触碰；分派走字符串 DriverKey、provider 与测试按名字取常量，仓库里没有一处读这颗整数）。
    //   它是 PG 血统的第七颗叶子（挂 PostgreSqlBase），而这一族文本层第一次真的改了血统的产出：
    //   类型表四格换名（DOUBLE/TIMESTAMP/TIMESTAMPTZ/TIME）、身份列换形态（句外 CREATE SEQUENCE '…'
    //   + DEFAULT nextval('…')）、"建库"整格不存在（内嵌单文件引擎，与 SQLite 同档）—— 逐格出处见
    //   src/QuickSql.Providers.DuckDb 各类注释与设计文档 §9.1/§13.5；整层【文档·DuckDB】口径、未实测。
    DuckDb = 16,
}
