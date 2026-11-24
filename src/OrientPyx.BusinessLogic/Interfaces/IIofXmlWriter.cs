using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Serialises an IOF 3.0 document tree to the bytes of an XML file (UTF-8, no BOM, the element order of
/// <c>IOF.xsd</c>). Implemented in DataAccess, like the other output writers.
/// </summary>
public interface IIofXmlWriter
{
    byte[] Write(IofResultList list);

    byte[] Write(IofStartList list);
}
