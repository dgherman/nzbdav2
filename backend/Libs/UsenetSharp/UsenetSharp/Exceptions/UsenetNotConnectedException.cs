namespace UsenetSharp.Exceptions;

public class UsenetNotConnectedException(string errorMessage) : UsenetException(errorMessage)
{
}