using System;

namespace ClickOnceCore
{
    public class ClickOnceDeploymentException : Exception
    {
        public ClickOnceDeploymentException(string message) : base(message)
        {
        }
    }
}
