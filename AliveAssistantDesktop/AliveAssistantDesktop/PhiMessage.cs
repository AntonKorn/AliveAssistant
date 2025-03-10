using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AliveAssistantDesktop
{
    public class PhiMessage
    {
        public string Text { get; set; }
        public PhiMessageType Type { get; set; }

        public PhiMessage(string text, PhiMessageType type)
        {
            Text = text;
            Type = type;
        }
    }

    public enum PhiMessageType
    {
        User,
        Assistant
    }
}
