namespace AiMemoryManager.Models;

public record PromptTemplate
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Content { get; set; }
    public bool IsDefault { get; set; }
    public bool IsBuiltin { get; init; }           // 出厂模板 true,可编辑但可恢复
}
