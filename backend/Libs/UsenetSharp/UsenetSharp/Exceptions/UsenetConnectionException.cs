namespace UsenetSharp.Exceptions;

public class UsenetConnectionException(string errorMessage) : UsenetException(errorMessage)
{
}