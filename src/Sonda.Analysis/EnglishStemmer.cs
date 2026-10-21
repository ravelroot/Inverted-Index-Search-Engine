namespace Sonda.Analysis;

public sealed class EnglishStemmer : IStemmer
{
    public Language Language => Language.English;

    public string Stem(string word)
    {
        if (word.Length <= 2)
        {
            return word;
        }

        var state = new PorterState(word);
        state.Step1AB();
        if (state.K > 0)
        {
            state.Step1C();
            state.Step2();
            state.Step3();
            state.Step4();
            state.Step5();
        }

        return new string(state.Buffer, 0, state.K + 1);
    }

    private sealed class PorterState
    {
        public readonly char[] Buffer;
        public int K;
        private int _j;

        public PorterState(string word)
        {
            Buffer = new char[word.Length + 8];
            word.CopyTo(0, Buffer, 0, word.Length);
            K = word.Length - 1;
        }

        private bool IsConsonant(int i)
        {
            switch (Buffer[i])
            {
                case 'a':
                case 'e':
                case 'i':
                case 'o':
                case 'u':
                    return false;
                case 'y':
                    return i == 0 || !IsConsonant(i - 1);
                default:
                    return true;
            }
        }

        private int Measure()
        {
            var n = 0;
            var i = 0;
            while (true)
            {
                if (i > _j)
                {
                    return n;
                }

                if (!IsConsonant(i))
                {
                    break;
                }

                i++;
            }

            i++;
            while (true)
            {
                while (true)
                {
                    if (i > _j)
                    {
                        return n;
                    }

                    if (IsConsonant(i))
                    {
                        break;
                    }

                    i++;
                }

                i++;
                n++;
                while (true)
                {
                    if (i > _j)
                    {
                        return n;
                    }

                    if (!IsConsonant(i))
                    {
                        break;
                    }

                    i++;
                }

                i++;
            }
        }

        private bool VowelInStem()
        {
            for (var i = 0; i <= _j; i++)
            {
                if (!IsConsonant(i))
                {
                    return true;
                }
            }

            return false;
        }

        private bool DoubleConsonant(int j) => j >= 1 && Buffer[j] == Buffer[j - 1] && IsConsonant(j);

        private bool ConsonantVowelConsonant(int i)
        {
            if (i < 2 || !IsConsonant(i) || IsConsonant(i - 1) || !IsConsonant(i - 2))
            {
                return false;
            }

            var ch = Buffer[i];
            return ch != 'w' && ch != 'x' && ch != 'y';
        }

        private bool Ends(string suffix)
        {
            var length = suffix.Length;
            if (length > K + 1)
            {
                return false;
            }

            if (suffix[length - 1] != Buffer[K])
            {
                return false;
            }

            var offset = K - length + 1;
            for (var i = 0; i < length; i++)
            {
                if (Buffer[offset + i] != suffix[i])
                {
                    return false;
                }
            }

            _j = K - length;
            return true;
        }

        private void SetTo(string replacement)
        {
            var offset = _j + 1;
            for (var i = 0; i < replacement.Length; i++)
            {
                Buffer[offset + i] = replacement[i];
            }

            K = _j + replacement.Length;
        }

        private void ReplaceIfMeasured(string replacement)
        {
            if (Measure() > 0)
            {
                SetTo(replacement);
            }
        }

        public void Step1AB()
        {
            if (Buffer[K] == 's')
            {
                if (Ends("sses"))
                {
                    K -= 2;
                }
                else if (Ends("ies"))
                {
                    SetTo("i");
                }
                else if (K >= 1 && Buffer[K - 1] != 's')
                {
                    K--;
                }
            }

            if (Ends("eed"))
            {
                if (Measure() > 0)
                {
                    K--;
                }
            }
            else if ((Ends("ed") || Ends("ing")) && VowelInStem())
            {
                K = _j;
                if (Ends("at"))
                {
                    SetTo("ate");
                }
                else if (Ends("bl"))
                {
                    SetTo("ble");
                }
                else if (Ends("iz"))
                {
                    SetTo("ize");
                }
                else if (DoubleConsonant(K))
                {
                    K--;
                    var ch = Buffer[K];
                    if (ch is 'l' or 's' or 'z')
                    {
                        K++;
                    }
                }
                else
                {
                    _j = K;
                    if (Measure() == 1 && ConsonantVowelConsonant(K))
                    {
                        SetTo("e");
                    }
                }
            }
        }

        public void Step1C()
        {
            if (Ends("y") && VowelInStem())
            {
                Buffer[K] = 'i';
            }
        }

        public void Step2()
        {
            if (K < 1)
            {
                return;
            }

            switch (Buffer[K - 1])
            {
                case 'a':
                    if (Ends("ational")) { ReplaceIfMeasured("ate"); }
                    else if (Ends("tional")) { ReplaceIfMeasured("tion"); }
                    break;
                case 'c':
                    if (Ends("enci")) { ReplaceIfMeasured("ence"); }
                    else if (Ends("anci")) { ReplaceIfMeasured("ance"); }
                    break;
                case 'e':
                    if (Ends("izer")) { ReplaceIfMeasured("ize"); }
                    break;
                case 'l':
                    if (Ends("bli")) { ReplaceIfMeasured("ble"); }
                    else if (Ends("alli")) { ReplaceIfMeasured("al"); }
                    else if (Ends("entli")) { ReplaceIfMeasured("ent"); }
                    else if (Ends("eli")) { ReplaceIfMeasured("e"); }
                    else if (Ends("ousli")) { ReplaceIfMeasured("ous"); }
                    break;
                case 'o':
                    if (Ends("ization")) { ReplaceIfMeasured("ize"); }
                    else if (Ends("ation")) { ReplaceIfMeasured("ate"); }
                    else if (Ends("ator")) { ReplaceIfMeasured("ate"); }
                    break;
                case 's':
                    if (Ends("alism")) { ReplaceIfMeasured("al"); }
                    else if (Ends("iveness")) { ReplaceIfMeasured("ive"); }
                    else if (Ends("fulness")) { ReplaceIfMeasured("ful"); }
                    else if (Ends("ousness")) { ReplaceIfMeasured("ous"); }
                    break;
                case 't':
                    if (Ends("aliti")) { ReplaceIfMeasured("al"); }
                    else if (Ends("iviti")) { ReplaceIfMeasured("ive"); }
                    else if (Ends("biliti")) { ReplaceIfMeasured("ble"); }
                    break;
                case 'g':
                    if (Ends("logi")) { ReplaceIfMeasured("log"); }
                    break;
            }
        }

        public void Step3()
        {
            switch (Buffer[K])
            {
                case 'e':
                    if (Ends("icate")) { ReplaceIfMeasured("ic"); }
                    else if (Ends("ative")) { ReplaceIfMeasured(string.Empty); }
                    else if (Ends("alize")) { ReplaceIfMeasured("al"); }
                    break;
                case 'i':
                    if (Ends("iciti")) { ReplaceIfMeasured("ic"); }
                    break;
                case 'l':
                    if (Ends("ical")) { ReplaceIfMeasured("ic"); }
                    else if (Ends("ful")) { ReplaceIfMeasured(string.Empty); }
                    break;
                case 's':
                    if (Ends("ness")) { ReplaceIfMeasured(string.Empty); }
                    break;
            }
        }

        public void Step4()
        {
            if (K < 1)
            {
                return;
            }

            var matched = Buffer[K - 1] switch
            {
                'a' => Ends("al"),
                'c' => Ends("ance") || Ends("ence"),
                'e' => Ends("er"),
                'i' => Ends("ic"),
                'l' => Ends("able") || Ends("ible"),
                'n' => Ends("ant") || Ends("ement") || Ends("ment") || Ends("ent"),
                'o' => (Ends("ion") && _j >= 0 && (Buffer[_j] == 's' || Buffer[_j] == 't')) || Ends("ou"),
                's' => Ends("ism"),
                't' => Ends("ate") || Ends("iti"),
                'u' => Ends("ous"),
                'v' => Ends("ive"),
                'z' => Ends("ize"),
                _ => false,
            };

            if (matched && Measure() > 1)
            {
                K = _j;
            }
        }

        public void Step5()
        {
            _j = K;
            if (Buffer[K] == 'e')
            {
                var measure = Measure();
                if (measure > 1 || (measure == 1 && !ConsonantVowelConsonant(K - 1)))
                {
                    K--;
                }
            }

            if (Buffer[K] == 'l' && DoubleConsonant(K) && Measure() > 1)
            {
                K--;
            }
        }
    }
}
