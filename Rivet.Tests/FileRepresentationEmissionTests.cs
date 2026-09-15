namespace Rivet.Tests;

public sealed class FileRepresentationEmissionTests
{
    [Theory]
    [InlineData("Define.File(\"/image\").ContentType(\"image/png\")", 200)]
    [InlineData("Define.Get(\"/image\").ProducesFile(\"image/png\")", 200)]
    [InlineData("Define.File(\"/image\").ContentType(\"image/png\").Status(201)", 201)]
    public void Additional_representation_preserves_factory_default(string factory, int status)
    {
        var source = $$"""
            using Rivet;
            [RivetContract]
            public static class ImagesContract
            {
                public static readonly Define Image = {{factory}}
                    .ResponseBinaryContent({{status}}, "image/jpeg");
            }
            """;
        var (endpoints, _) = CompilationHelper.WalkContract(source);
        var response = Assert.Single(
            Assert.Single(endpoints).Responses,
            response => response.StatusCode == status
        );
        Assert.Equal(
            ["image/jpeg", "image/png"],
            response.Contents!.Select(content => content.MediaType).Order()
        );
        Assert.All(response.Contents!, content => Assert.True(content.IsBinary));
    }
}
