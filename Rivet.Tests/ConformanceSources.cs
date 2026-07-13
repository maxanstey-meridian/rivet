namespace Rivet.Tests;

internal static class ConformanceSources
{
    internal const string MaximalContract = """
        using System;
        using System.Collections.Generic;
        using Microsoft.AspNetCore.Http;
        using Rivet;

        namespace Test;

        public enum Priority { Low, Medium, High, Critical }

        [RivetType]
        public sealed record Email(string Value);

        [RivetType]
        public sealed record Uprn(string Value);

        [RivetType]
        public sealed record Quantity(int Value);

        [RivetType]
        public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

        [RivetType]
        public sealed record KitchenSinkDto(
            string Name,
            int IntVal,
            uint UintVal,
            long LongVal,
            ulong UlongVal,
            short ShortVal,
            ushort UshortVal,
            byte ByteVal,
            sbyte SbyteVal,
            float FloatVal,
            double DoubleVal,
            decimal DecimalVal,
            bool BoolVal,
            DateTime DateTimeVal,
            DateTimeOffset DateTimeOffsetVal,
            DateOnly DateOnlyVal,
            Guid GuidVal,
            char CharVal,
            object ObjectVal,
            string? NullableString,
            int? NullableInt,
            bool? NullableBool,
            Guid? NullableGuid,
            DateTime? NullableDateTime,
            DateTimeOffset? NullableDateTimeOffset,
            char? NullableChar,
            object? NullableObject,
            List<string> Tags,
            List<int> Scores,
            Dictionary<string, string> Metadata,
            Dictionary<string, int> Counts,
            Dictionary<Priority, int> PriorityTallies,
            Dictionary<char, int> InitialTallies,
            List<Guid> IdList,
            Email AuthorEmail,
            Quantity ItemQuantity,
            Priority CurrentPriority,
            AddressDto HomeAddress,
            AddressDto? WorkAddress,
            [property: Obsolete] string LegacyField);

        [RivetType]
        public sealed record AddressDto(string Line1, string? Line2, string City, string PostCode);

        [RivetType]
        public sealed record NotFoundError(string Message);

        [RivetType]
        public sealed record ValidationError(string Message, Dictionary<string, string> Errors);

        [RivetType]
        public sealed record CreateItemInput(
            string Name, Email AuthorEmail, Priority CurrentPriority, AddressDto HomeAddress);

        [RivetType]
        public sealed record SearchInput(string Query, int Limit, int? Offset);

        [RivetType]
        public sealed record UploadInput(IFormFile Document, string Title, int? PageCount);

        [RivetType]
        public sealed record UploadResult(string Url, Guid FileId);

        [RivetType]
        public sealed record UserDto(string Id, string Name, Email Email, Uprn? Uprn);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly Define GetItem =
                Define.Get<KitchenSinkDto>("/api/items/{id}")
                    .Description("Retrieve a single item by its unique ID");

            public static readonly Define SearchItems =
                Define.Get<SearchInput, PagedResult<KitchenSinkDto>>("/api/items");

            public static readonly Define CreateItem =
                Define.Post<CreateItemInput, KitchenSinkDto>("/api/items")
                    .Status(201)
                    .Returns<ValidationError>(422, "Validation failed");

            public static readonly Define UpdateItem =
                Define.Put<CreateItemInput, KitchenSinkDto>("/api/items/{id}");

            public static readonly Define DeleteItem =
                Define.Delete("/api/items/{id}")
                    .Status(204)
                    .Returns<NotFoundError>(404, "Item not found");

            public static readonly Define PatchItem =
                Define.Patch<CreateItemInput>("/api/items/{id}")
                    .Status(204);
        }

        [RivetContract]
        public static class UsersContract
        {
            public static readonly Define ListUsers =
                Define.Get<PagedResult<UserDto>>("/api/users")
                    .Description("List all users with pagination");

            public static readonly Define GetUser =
                Define.Get<UserDto>("/api/users/{userId}")
                    .Returns<NotFoundError>(404, "User not found");
        }

        [RivetContract]
        public static class FilesContract
        {
            public static readonly Define Upload =
                Define.Post<UploadInput, UploadResult>("/api/files")
                    .Status(201);
        }

        [RivetContract]
        public static class HealthContract
        {
            public static readonly Define Check =
                Define.Get("/api/health")
                    .Anonymous()
                    .Description("Health check endpoint");
        }

        [RivetContract]
        public static class AdminContract
        {
            public static readonly Define Purge =
                Define.Delete("/api/admin/cache")
                    .Status(204)
                    .Secure("admin");
        }
        """;
}
