using System;
using System.IO;
using System.Net;
using System.Web;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Security.Cryptography;
using MTLWB.Common;

namespace MTLWB.HxS
{
    internal class HtmlDocument : HtmlAgilityPack.HtmlDocument
    {
        public readonly Uri URI;
        public readonly string Title;
        public readonly CultureInfo CultureInfo;
        public readonly LinkedList<string[]> TranslationRequests;
        int sentenceCount;
        public readonly MarkupMode Mode;
        public readonly List<string> MarkupExpressions;

        //public Data dataConnection;

        //readonly HtmlAgilityPack.HtmlNode m_hnHead, m_hnTitle, m_hnBody;
        readonly HtmlAgilityPack.HtmlNode m_hnHead, m_hnBody;
        readonly TranslationType TranslationType;
        readonly bool CommunityContent;

        public HtmlDocument(string filePath, CultureInfo ci, MarkupMode mode, List<string> expressions, TranslationType translationType, bool communityContent)
        {
            this.CultureInfo = ci;
            this.Mode = mode;
            this.TranslationType = translationType;
            this.CommunityContent = communityContent;

            URI = new Uri(filePath);

            MarkupExpressions = expressions;

            //dataConnection = new Data(); 

            sentenceCount = 1;

            // Get the HTML from the file.
            StreamReader sr = File.OpenText(filePath);
            string html = sr.ReadToEnd();
            sr.Close();

            // This loads the file into the HtmlDocument.  The complete text of the file is assigned to the 
            // DocumentNode property.
            LoadHtml(html);

            {
                HtmlAgilityPack.HtmlNodeCollection hnc = DocumentNode.SelectNodes("//title", false);
                Title = hnc != null && hnc.Count > 0 ? HttpUtility.HtmlDecode(hnc[0].InnerHtml) : "";

                hnc = DocumentNode.SelectNodes("//head", false);
                if (hnc != null && hnc.Count > 0)
                    m_hnHead = hnc[0];
                else
                    m_hnHead = DocumentNode;

                // Get the main section of the page.  Sometimes called "mainSection", sometimes "mainBody".
                // hnc = DocumentNode.SelectNodes("//div[@id='mainSection']");
                hnc = DocumentNode.SelectNodes("//topic", false);
                if (hnc == null)
                {
                    hnc = DocumentNode.SelectNodes("//div[@id='mainbody']", false);
                    if (hnc == null)
                    {
                        hnc = DocumentNode.SelectNodes("//body", false);
                        if (hnc == null)
                        {
                            m_hnBody = DocumentNode;
                        }
                        else
                        {
                            m_hnBody = hnc[0];
                        }
                    }
                    else
                    {
                        m_hnBody = hnc[0];
                    }
                }
                else
                {
                    m_hnBody = hnc[0];
                }

                //TEJAS: Quick fix for Beta1 release. Should be taken out later. | May 2009
                if (m_hnBody != null)
                {
                    hnc = m_hnBody.SelectNodes("//div[@class='code']", false);
                    if (hnc != null)
                    {
                        foreach (HtmlAgilityPack.HtmlNode hn in hnc)
                        {
                            HtmlAgilityPack.HtmlNode tempNode = hn.OwnerDocument.CreateElement("NoLocCodeContent");
                            tempNode.AppendChildren(hn.ChildNodes);
                            hn.RemoveAllChildren();
                            hn.AppendChild(tempNode);
                            //hn.InnerHtml = "<NoLocCodeContent>" + hn.InnerHtml + "</NoLocCodeContent>";
                        }
                    }
                }
            }
            //m_hnTitle = DocumentNode.SelectNodes("//span[@id='nsrTitle']")[0];  
            if (Mode == MarkupMode.Output)
            {
                InjectMarkUp();
            }

            TranslationRequests = new LinkedList<string[]>();
            ChunkAndAnnotate();

            // MarkupLog.statsMarkupSW.WriteLine("<sentenceCount>" + sentenceCount.ToString() + "</sentenceCount>");
        }

        #region Private Methods

        private void ChunkAndAnnotate()
        {
            LinkedList<LinkedList<HtmlAgilityPack.HtmlNode>> liChunks = new LinkedList<LinkedList<HtmlAgilityPack.HtmlNode>>();
            ChunkDOM(m_hnBody, liChunks);

            LinkedList<LinkedList<HtmlAgilityPack.HtmlNode>> liRequests = new LinkedList<LinkedList<HtmlAgilityPack.HtmlNode>>();
            int iReqSize = Statics.RequestSoftCharLimit;
            foreach (LinkedList<HtmlAgilityPack.HtmlNode> liChunk in liChunks)
            {
                foreach (HtmlAgilityPack.HtmlNode hnChunk in Chunkify(liChunk))
                {
                    if (hnChunk.InnerHtml.Length <= Statics.RequestHardCharLimit)
                    {
                        if ((iReqSize > 0 && iReqSize + hnChunk.InnerHtml.Length >= Statics.RequestSoftCharLimit) ||
                            liRequests.Last.Value.Count >= Statics.RequestArraySizeLimit)
                        {
                            liRequests.AddLast(new LinkedList<HtmlAgilityPack.HtmlNode>());
                            iReqSize = 0;
                        }
                        liRequests.Last.Value.AddLast(hnChunk);
                        iReqSize += hnChunk.InnerHtml.Length;
                    }
                    else
                    {
                        // ToDo: Handle oversized chunk/sentence
                    }
                }
            }

            foreach (LinkedList<HtmlAgilityPack.HtmlNode> liRequest in liRequests)
            {
                string[] ReqStrings = new string[liRequest.Count];
                //int iOffset = 0;
                foreach (HtmlAgilityPack.HtmlNode hn in liRequest)
                {
                    //if (hn.ChildNodes[0].Name != "mtps:sentence")
                    //{
                    if (Mode == MarkupMode.Output)
                    {
                        // Generate MD5 hash ID.
                        string id = GenerateID(hn.InnerHtml);

                        hn.InnerHtml = "<span id=\"tgt" + sentenceCount + "\" sentenceId=\""
                        + id + "\" class=\"tgtSentence\">"
                            + hn.InnerHtml.Trim() + "</span>";
                        sentenceCount++;
                    }
                    else
                    {
                        // Generate MD5 hash ID.
                        string id = GenerateID(hn.InnerHtml);

                        hn.InnerHtml = "<span id=\"src" + sentenceCount + "\" class=\"srcSentence\">"
                                + hn.InnerHtml + "</span>";
                        sentenceCount++;
                    }
                }
                TranslationRequests.AddLast(ReqStrings);
            }
        }

        // Hash an input string and return the hash as
        // a 32 character hexadecimal string.
        private string GenerateID(string input)
        {
            // Normalize the input string.
            string normalizedInput = Normalize(input);

            // Create a new instance of the MD5CryptoServiceProvider object.
            MD5CryptoServiceProvider md5Hasher = new MD5CryptoServiceProvider();

            // Convert the input string to a byte array and compute the hash.
            byte[] data = md5Hasher.ComputeHash(Encoding.Default.GetBytes(normalizedInput));

            // Create a new Stringbuilder to collect the bytes
            // and create a string.
            StringBuilder sBuilder = new StringBuilder();

            // Loop through each byte of the hashed data 
            // and format each one as a hexadecimal string.
            for (int i = 0; i < data.Length; i++)
            {
                sBuilder.Append(data[i].ToString("x2"));
            }

            // Return the hexadecimal string.
            return sBuilder.ToString();
        }
        private string Normalize(string sentence)
        {
            string normalizedSentence = sentence.ToLowerInvariant();
            return normalizedSentence;
        }

        private void InjectMarkUp()
        {
            string[] str = { "<mshelp:attr name=\"locale\" value=\"\"></mshelp:attr>", "<mshelp:attr name=\"locale\" value=\"en-us\"></mshelp:attr>", "<MSHelp:Attr name=\"locale\" value=\"kbenglish\"></MSHelp:Attr>" };
            int index = 0;
            int position = -1;
            string mshelpElements = "<MSHelp:Attr name=\"Locale\" Value=\"" + this.CultureInfo.Name + "\"></MSHelp:Attr> <MSHelp:Attr Name=\"TranslationType\" value=\"" + this.TranslationType + "\"></MSHelp:Attr><MSHelp:Attr Name=\"TranslationDate\" Value=\"" + DateTime.Today.ToShortDateString() + "\" /><MSHelp:Attr Name=\"TranslationSourceLocale\" Value=\"EN-US\" />" + (this.CommunityContent ? "<MSHelp:Attr Name=\"CommunityContent\" Value=\"1\" />" : "");
            while (!((position = m_hnHead.InnerHtml.ToLowerInvariant().IndexOf(str[index])) >= 0))
            {
                index++;
                if (index >= str.Length)
                    break;
            }

            if (position >= 0)
            {
                m_hnHead.InnerHtml = m_hnHead.InnerHtml.Remove(position, str[index].Length).Insert(position, mshelpElements);
            }
            else
            {
                position = m_hnHead.InnerHtml.IndexOf("</xml>");
                if (position >= 0)
                    m_hnHead.InnerHtml = m_hnHead.InnerHtml.Insert(position, mshelpElements);
            }
        }


        private void ChunkDOM(HtmlAgilityPack.HtmlNode hn, LinkedList<LinkedList<HtmlAgilityPack.HtmlNode>> liChunks)
        {
            bool bInChunk = false;
            LinkedList<HtmlAgilityPack.HtmlNode> liChunk = null;
            foreach (HtmlAgilityPack.HtmlNode hnChild in hn.ChildNodes)
            {
                if (!IsTerminal(hnChild))
                {
                    if (bInChunk)
                    {
                        while (liChunk.Count > 0 && liChunk.Last.Value.NodeType == HtmlAgilityPack.HtmlNodeType.Text &&
                            Regex.IsMatch(HttpUtility.HtmlDecode(liChunk.Last.Value.OuterHtml), @"^\s*$"))
                            liChunk.RemoveLast();
                        liChunks.AddLast(liChunk);
                        bInChunk = false;
                    }

                    if (IsValidNodeForMarkup(hnChild))
                        ChunkDOM(hnChild, liChunks);
                }
                else if (hnChild.NodeType == HtmlAgilityPack.HtmlNodeType.Text ||
                    (hnChild.NodeType == HtmlAgilityPack.HtmlNodeType.Element &&
                    Statics.IntraSententialTag(hnChild.Name)))
                {
                    if (!bInChunk)
                    {
                        if (hnChild.NodeType == HtmlAgilityPack.HtmlNodeType.Text &&
                            Regex.IsMatch(HttpUtility.HtmlDecode(hnChild.OuterHtml), @"^\s*$"))
                            continue;
                        liChunk = new LinkedList<HtmlAgilityPack.HtmlNode>();
                        bInChunk = true;
                    }

                    liChunk.AddLast(hnChild);
                }
            }
            if (bInChunk)
            {
                if (liChunk.Last.Value.NodeType == HtmlAgilityPack.HtmlNodeType.Text &&
                    Regex.IsMatch(HttpUtility.HtmlDecode(liChunk.Last.Value.OuterHtml), @"^\s*$"))
                    liChunk.RemoveLast();
                liChunks.AddLast(liChunk);
            }
        }
        /// <summary> 
        /// Runs through a list of various types of nodes that should not be marked up.  
        /// Most items on the list are pulled from the MTMarkup.config file.
        /// </summary>
        /// <param name="hnChild"></param>
        /// <returns>true if the node is valid for markup for futher processing; otherwise, false.</returns>
        private bool IsValidNodeForMarkup(HtmlAgilityPack.HtmlNode hnChild)
        {
            // return false if the node has no child nodes, the node doesn't contain any localizable text, or the node
            // is of type script or style.
            if (hnChild.ChildNodes.Count == 0 ||
                Regex.IsMatch(hnChild.InnerHtml, @"^\s*$") ||
                hnChild.Name == "script" ||
                hnChild.Name == "style")
                return false;

            // return false if the node matches any of the xpath expressions specified in the MTMarkup.config file.
            foreach (string s in MarkupExpressions)
            {
                if (hnChild.SelectNodes(s, true) != null)
                    return false;
            }

            return true;
        }

        static bool IsTerminal(HtmlAgilityPack.HtmlNode hn)
        {
            switch (hn.NodeType)
            {
                case HtmlAgilityPack.HtmlNodeType.Element:
                    //if (!Statics.IntraSententialTag(hn.Name.ToLower()))
                    if (!Statics.IntraSententialTag(hn.Name))
                        return false;
                    foreach (HtmlAgilityPack.HtmlNode hnChild in hn.ChildNodes)
                        if (!IsTerminal(hnChild))
                            return false;
                    return true;

                case HtmlAgilityPack.HtmlNodeType.Text:
                    string strText = HttpUtility.HtmlDecode(hn.OuterHtml);
                    if (Regex.IsMatch(strText, @"^\s*\|\s*$") ||
                        Regex.IsMatch(strText, @"^\s*\│\s*$") ||
                        Regex.IsMatch(strText, @"^\s\s+$") ||
                        Regex.IsMatch(strText, @"^\s+-\s+$"))
                        return false;
                    return true;

                default:
                    return true;
            }
        }

        LinkedList<HtmlAgilityPack.HtmlNode> Chunkify(LinkedList<HtmlAgilityPack.HtmlNode> liChunk)
        {
            if (liChunk.Count < 1)
                throw new Exception("Expected non-empty input list");

            TrimChunk(liChunk);

            LinkedList<HtmlAgilityPack.HtmlNode> liSentenceNodes = new LinkedList<HtmlAgilityPack.HtmlNode>();

            if (liChunk.Count < 1)
                return liSentenceNodes;

            StringBuilder sbChunk = new StringBuilder();
            foreach (HtmlAgilityPack.HtmlNode hn in liChunk)
            {
                //sbChunk.Append(HttpUtility.HtmlDecode(hn.OuterHtml));
                sbChunk.Append(hn.OuterHtml);
            }

            LinkedList<string> liSentenceStrings = SentenceBreaker.Break(sbChunk.ToString(), CultureInfo);

            if (liSentenceStrings.Count == 1)
            {
                HtmlAgilityPack.HtmlNode hnChunk;
                if (liChunk.Count == 1 && liChunk.First.Value.NodeType == HtmlAgilityPack.HtmlNodeType.Element)
                {
                    hnChunk = liChunk.First.Value;
                }
                else if (liChunk.Count == 1 && liChunk.First.Value.ParentNode.ChildNodes.Count == 1)
                {
                    hnChunk = liChunk.First.Value.ParentNode;
                }
                else
                {
                    HtmlAgilityPack.HtmlNode hnParent = liChunk.First.Value.ParentNode;
                    hnChunk = hnParent.OwnerDocument.CreateElement("span");

                    hnParent.InsertBefore(hnChunk, liChunk.First.Value);
                    foreach (HtmlAgilityPack.HtmlNode hn in liChunk)
                    {
                        hnParent.RemoveChild(hn);
                        hnChunk.AppendChild(hn);
                    }
                }
                liSentenceNodes.AddLast(hnChunk);
            }
            else if (liSentenceStrings.Count > 1)
            {
                HtmlAgilityPack.HtmlNode hnParent = liChunk.First.Value.ParentNode;

                foreach (string strSentence in liSentenceStrings)
                {
                    //HtmlAgilityPack.HtmlNode hnChunk = hnParent.OwnerDocument.CreateElement("mtps:sentence");
                    HtmlAgilityPack.HtmlNode hnChunk = hnParent.OwnerDocument.CreateTextNode(HttpUtility.HtmlEncode(" "));

                    // Generate MD5 hash ID.
                    //string id = GenerateID(HttpUtility.HtmlDecode(strSentence).Trim());
                    //hnChunk.Attributes.Append("runat", "server");
                    //hnChunk.Attributes.Append("id", "en_us_" + id);

                    //hnChunk.InnerHtml = HttpUtility.HtmlDecode(strSentence).Trim();
                    hnChunk.InnerHtml = strSentence.Trim();
                    hnParent.InsertBefore(hnChunk, liChunk.First.Value);
                    HtmlAgilityPack.HtmlNode hnText = hnParent.OwnerDocument.CreateTextNode(HttpUtility.HtmlEncode("  "));
                    hnParent.InsertAfter(hnText, hnChunk);
                    liSentenceNodes.AddLast(hnChunk);
                }

                foreach (HtmlAgilityPack.HtmlNode hn in liChunk)
                    hnParent.RemoveChild(hn);
            }
            else
            {
                // ToDo: Figure out why our chunk contains no sentences?
            }

            return liSentenceNodes;
        }

        void TrimChunk(LinkedList<HtmlAgilityPack.HtmlNode> liChunk)
        {
            bool bContinue;
            do
            {
                bContinue = false;
                while (liChunk.Count > 0 && Regex.IsMatch(liChunk.First.Value.InnerText, @"^\s*$"))
                {
                    liChunk.RemoveFirst();
                    bContinue = true;
                }

                while (liChunk.Count > 0 && Regex.IsMatch(liChunk.Last.Value.InnerText, @"^\s*$"))
                {
                    liChunk.RemoveLast();
                    bContinue = true;
                }

                if (liChunk.Count == 1 && liChunk.First.Value.NodeType == HtmlAgilityPack.HtmlNodeType.Element)
                {
                    foreach (HtmlAgilityPack.HtmlNode hnChild in liChunk.First.Value.ChildNodes)
                    {
                        liChunk.AddLast(hnChild);
                    }
                    liChunk.RemoveFirst();
                    bContinue = true;
                }
            }
            while (bContinue);
        }

        #endregion
    }
}
