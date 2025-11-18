using System.Security.Cryptography.X509Certificates;

namespace ProcessorApplication.Sqlite.Models;

/**
 * tracker is public-share node
 * it shares other trackers with other trackers and a list of known algorithms 
 * between local peers and other known nodes
 * trackers keep list of shareable (algorithms, processes)
 */
public class Tracker : Peer
{
    // port is typically 5000
}
