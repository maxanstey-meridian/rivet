using System;
using System.Collections.Generic;
using Rivet;

namespace CaseBridge.Application.Submissions;

public enum MessageVisibility { Internal, Public }

public enum SubmissionStatus { Draft, Submitted, UnderReview, Approved, Rejected }

public sealed record Address(string Line1, string? Line2, string City, string Postcode);

[RivetType]
public sealed record CreateMessageCommand(
    Guid SubmissionId,
    string Body,
    MessageVisibility Visibility);

[RivetType]
public sealed record MessageDto(
    Guid Id,
    string Body,
    string AuthorName,
    DateTime CreatedAt);

[RivetType]
public sealed record SubmissionDetailDto(
    Guid Id,
    string Reference,
    SubmissionStatus Status,
    Address CorrespondenceAddress,
    List<MessageDto> Messages,
    Dictionary<string, string> Metadata,
    DateTime? ClosedAt);
