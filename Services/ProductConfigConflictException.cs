namespace PartMap.Services;

public sealed class ProductConfigConflictException : IOException
{
    public ProductConfigConflictException()
        : base("另一台电脑已修改此产品配置。为防止覆盖，当前修改没有保存。")
    {
    }
}
