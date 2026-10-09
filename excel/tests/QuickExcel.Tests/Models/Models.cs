using System.ComponentModel.DataAnnotations;

namespace QuickExcel.Tests.Models;

public class Employee
{
    [ExcelColumn("工号", Order = 1)]
    [Required(ErrorMessage = "工号必填")]
    public int Id { get; set; }

    [ExcelColumn("姓名", Order = 2, Width = 18)]
    [Required]
    [StringLength(20, ErrorMessage = "姓名不能超过20字")]
    public string Name { get; set; } = "";

    [ExcelColumn("部门", Order = 3, Aliases = new[] { "Dept", "所属部门" })]
    public string? Department { get; set; }

    [ExcelColumn("薪资", Order = 4)]
    [Range(0, 1_000_000, ErrorMessage = "薪资范围非法")]
    [ExcelFormat("#,##0.00")]
    public decimal Salary { get; set; }

    [ExcelColumn("入职日期", Order = 5)]
    [ExcelFormat("yyyy-MM-dd")]
    public DateTime HireDate { get; set; }

    [ExcelColumn("在职", Order = 6)]
    public bool Active { get; set; }

    [ExcelColumn("职级", Order = 7)]
    public Level Level { get; set; }

    [ExcelColumn("备注", Order = 8)]
    public string? Note { get; set; }

    [ExcelIgnore]
    public string InternalCode { get; set; } = "hidden";

    public override string ToString() => $"{Id}:{Name}:{Salary}:{HireDate:yyyy-MM-dd}:{Level}:{Active}";
}

public enum Level { Junior, Mid, Senior, Lead }

public class Photo
{
    [ExcelColumn("名称", Order = 1)]
    public string Name { get; set; } = "";

    [ExcelColumn("图片", Order = 2)]
    [ExcelImage]
    public ExcelImage? Pic { get; set; }
}

public class BytePhoto
{
    [ExcelColumn("名称", Order = 1)]
    public string Name { get; set; } = "";

    [ExcelColumn("缩略图", Order = 2)]
    [ExcelImage(Width = 96, Height = 72)]
    public byte[] Image { get; set; } = System.Array.Empty<byte>();
}

/// <summary>Implements IValidatableObject to exercise cross-field validation.</summary>
public class OrderLine : IValidatableObject
{
    [Required]
    public string Sku { get; set; } = "";
    [Range(1, 10000)]
    public int Qty { get; set; }
    public double UnitPrice { get; set; }
    public bool IsGift { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!IsGift && UnitPrice <= 0)
            yield return new ValidationResult("非赠品的单价必须大于 0", new[] { nameof(UnitPrice) });
    }
}
