namespace Common.Interfaces;
public interface ICapabilityProvider
{
    //capabilities that node may advertise
    IEnumerable<string> ListAdvertisedCapabilities();
}