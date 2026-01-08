namespace UsenetSharp.Models;

public enum UsenetResponseType
{
    Unknown = 0,
    DateAndTime = 111,
    ServerReadyPostingAllowed = 200,
    ServerReadyNoPostingAllowed = 201,
    ArticleRetrievedHeadAndBodyFollow = 220,
    ArticleRetrievedHeadFollows = 221,
    ArticleRetrievedBodyFollows = 222,
    ArticleExists = 223,
    AuthenticationAccepted = 281,
    PasswordRequired = 381,
    NoGroupSelected = 412,
    CurrentArticleInvalid = 420,
    NoArticleWithThatNumber = 423,
    NoArticleWithThatMessageId = 430,
    AuthenticationRequired = 480,
    AuthenticationRejected = 481,
    AuthenticationOutOfSequence = 482,
    AccessPermanentlyForbidden = 502,
}