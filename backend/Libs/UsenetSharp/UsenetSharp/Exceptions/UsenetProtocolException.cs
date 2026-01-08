namespace UsenetSharp.Exceptions;

public class UsenetProtocolException(string errorMessage) : Exception(errorMessage)
{
}