namespace QuickJob.Tests;

using QuickJob;
using System.Text;
using System.Text.Json;

/// <summary>
/// PayloadJson 管道：byte[] (UTF-8 JSON) 是运行形态，JSON 文本是存档形态，typed-request 只是
/// 反序列化台阶。顺带守两条家规：空载荷走 null（不是空 JSON），枚举在存档文本里保真字符串档。
/// </summary>
public class PayloadJsonTests
{
    public sealed class Cargo
    {
        public int Id { get; set; }
        public string? Note { get; set; }
        public string[]? Tags { get; set; }
        public JobPriority Priority { get; set; }
    }

    [Fact]
    public void Null_payload_stays_null_through_the_whole_pipe()
    {
        Assert.Empty(PayloadJson.Serialize(null));
        Assert.Null(PayloadJson.ToStoreText(PayloadJson.Serialize(null)));
        Assert.Null(PayloadJson.FromStoreText(null));

        Assert.Null(PayloadJson.Deserialize<Cargo>(null));
        Assert.Null(PayloadJson.Deserialize<Cargo>([]));
        Assert.Null(PayloadJson.FromStoreText(""));
    }

    [Fact]
    public void Roundtrip_carries_the_full_shape()
    {
        var cargo = new Cargo { Id = 7, Note = "hot", Tags = ["a", "b"], Priority = JobPriority.High };

        var bytes = PayloadJson.Serialize(cargo);
        Assert.NotEmpty(bytes);

        var store = PayloadJson.ToStoreText(bytes);
        Assert.NotNull(store);
        var back = PayloadJson.FromStoreText(store);

        var restored = PayloadJson.Deserialize<Cargo>(back);
        Assert.NotNull(restored);
        Assert.Equal(7, restored.Id);
        Assert.Equal("hot", restored.Note);
        var tags = restored.Tags;
        Assert.NotNull(tags);
        Assert.Equal(["a", "b"], tags);
        Assert.Equal(JobPriority.High, restored.Priority);
    }

    [Fact]
    public void Store_text_is_human_readable_json()
    {
        var cargo = new Cargo { Id = 1, Priority = JobPriority.Low };
        var store = PayloadJson.ToStoreText(PayloadJson.Serialize(cargo))!;

        // 合法 JSON 且枚举保真字符串（JsonStringEnumConverter 的存档口径）
        using var doc = JsonDocument.Parse(store);
        Assert.Equal("Low", doc.RootElement.GetProperty("priority").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Typed_deserialise_tolerates_extra_and_missing_rows()
    {
        var cargo = PayloadJson.Deserialize<Cargo>(Encoding.UTF8.GetBytes("""{"id":"9","note":"x","mystery":true}"""));
        Assert.NotNull(cargo);
        Assert.Equal(9, cargo.Id);
        Assert.Equal("x", cargo.Note);
        Assert.Null(cargo.Tags);
    }

    [Fact]
    public void Text_roundtrip_is_byte_exact()
    {
        var bytes = PayloadJson.Serialize(new Cargo { Id = 3 });
        var back = PayloadJson.FromStoreText(PayloadJson.ToStoreText(bytes));
        Assert.Equal(back, bytes);
    }
}
