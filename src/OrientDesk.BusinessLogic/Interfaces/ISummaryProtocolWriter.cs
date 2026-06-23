using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Renders a <see cref="SummaryProtocolDocument"/> (two-tier banded header) to a Word (.docx) file. Implemented
/// in DataAccess (the Open XML SDK must not be referenced from BusinessLogic).
/// </summary>
public interface ISummaryProtocolWriter
{
    byte[] Write(SummaryProtocolDocument document);
}
