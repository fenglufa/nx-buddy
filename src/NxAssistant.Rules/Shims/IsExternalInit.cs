// netstandard2.0 下启用 init-only 属性所需的编译器垫片（记录/初始化器）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
