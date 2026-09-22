using Xunit;
namespace ChronoTests;
public class SmokeTest { [Fact] public void Ok() => Assert.Equal("ok", ChronoCore.Marker.Ok); }
