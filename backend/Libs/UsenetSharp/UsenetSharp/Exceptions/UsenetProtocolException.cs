namespace UsenetSharp.Exceptions;

public class UsenetProtocolException(string errorMessage) : UsenetException(errorMessage)
{
}
