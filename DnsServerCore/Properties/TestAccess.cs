using System.Runtime.CompilerServices;

// Isolated protocol integration tests need cluster fixtures without opening public enrollment APIs.
[assembly: InternalsVisibleTo("Ipam.Tests")]
