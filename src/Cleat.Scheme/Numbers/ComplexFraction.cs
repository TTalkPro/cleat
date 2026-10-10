#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion


using System;

using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// Implementation of the complex number data type.
/// </summary>
[Serializable]
public class ComplexFraction
{
    private readonly Fraction _real, _imag;

    public static ComplexFraction MakeImaginary(Fraction imag)
    {
        return new ComplexFraction(0, imag);
    }

    public static ComplexFraction MakeReal(Fraction real)
    {
        return new ComplexFraction(real, 0);
    }

    public static ComplexFraction Make(Fraction real, Fraction imag)
    {
        return new ComplexFraction(real, imag);
    }

    public ComplexFraction(Fraction real)
      : this(real, 0)
    {
    }

    public ComplexFraction(Fraction real, Fraction imag)
    {
        _real = real;
        _imag = imag;
    }

    public Complex64 ToComplex64()
    {
        return this;
    }

    public bool IsZero
    {
        get
        {
            return _real == 0 && _imag == 0;
        }
    }

    public Fraction Real
    {
        get
        {
            return _real;
        }
    }

    public Fraction Imag
    {
        get
        {
            return _imag;
        }
    }

    public ComplexFraction Conjugate()
    {
        return new ComplexFraction(_real, 0 - _imag);
    }


    public override string ToString()
    {
        if (_real == 0) return _imag.ToString() + "i";
        else if (_imag < 0) return string.Format(System.Globalization.CultureInfo.InvariantCulture.NumberFormat, "{0}{1}i", _real, _imag);
        else return string.Format(System.Globalization.CultureInfo.InvariantCulture.NumberFormat, "{0}+{1}i", _real, _imag);
    }

    public static implicit operator ComplexFraction(int i)
    {
        return MakeReal(i);
    }



    public static implicit operator ComplexFraction(short i)
    {
        return MakeReal(i);
    }



    [CLSCompliant(false)]
    public static implicit operator ComplexFraction(sbyte i)
    {
        return MakeReal(i);
    }

    public static implicit operator ComplexFraction(byte i)
    {
        return MakeReal(i);
    }


    public static implicit operator ComplexFraction(BigInteger i)
    {
        return MakeReal(i);
    }

    public static bool operator ==(ComplexFraction x, ComplexFraction y)
    {
        return x._real == y._real && x._imag == y._imag;
    }

    public static bool operator !=(ComplexFraction x, ComplexFraction y)
    {
        return x._real != y._real || x._imag != y._imag;
    }

    public static ComplexFraction Add(ComplexFraction x, ComplexFraction y)
    {
        return x + y;
    }

    public static ComplexFraction operator +(ComplexFraction x, ComplexFraction y)
    {
        return new ComplexFraction(x._real + y._real, x._imag + y._imag);
    }

    public static ComplexFraction Subtract(ComplexFraction x, ComplexFraction y)
    {
        return x - y;
    }

    public static ComplexFraction operator -(ComplexFraction x, ComplexFraction y)
    {
        return new ComplexFraction(x._real - y._real, x._imag - y._imag);
    }

    public static ComplexFraction Multiply(ComplexFraction x, ComplexFraction y)
    {
        return x * y;
    }

    public static ComplexFraction operator *(ComplexFraction x, ComplexFraction y)
    {
        return new ComplexFraction(x._real * y._real - x._imag * y._imag, x._real * y._imag + x._imag * y._real);
    }

    public static ComplexFraction Divide(ComplexFraction x, ComplexFraction y)
    {
        return x / y;
    }

    public static ComplexFraction operator /(ComplexFraction a, ComplexFraction b)
    {
        if (b.IsZero) throw new DivideByZeroException();

        Fraction real, imag, den, r;

        if (b._real.Abs() >= b._imag.Abs())
        {
            r = b._imag / b._real;
            den = b._real + r * b._imag;
            real = (a._real + a._imag * r) / den;
            imag = (a._imag - a._real * r) / den;
        }
        else
        {
            r = b._real / b._imag;
            den = b._imag + r * b._real;
            real = (a._real * r + a._imag) / den;
            imag = (a._imag * r - a._real) / den;
        }

        return new ComplexFraction(real, imag);
    }

    public static ComplexFraction Mod(ComplexFraction x, ComplexFraction y)
    {
        return x % y;
    }

    public static ComplexFraction operator %(ComplexFraction x, ComplexFraction y)
    {
        if (ReferenceEquals(x, null))
        {
            throw new ArgumentNullException("x");
        }
        if (ReferenceEquals(y, null))
        {
            throw new ArgumentNullException("y");
        }

        if (y == 0) throw new DivideByZeroException();

        throw new NotImplementedException();
    }

    public static ComplexFraction Negate(ComplexFraction x)
    {
        return -x;
    }

    public static ComplexFraction operator -(ComplexFraction x)
    {
        return new ComplexFraction(0 - x._real, 0 - x._imag);
    }

    public static ComplexFraction Plus(ComplexFraction x)
    {
        return +x;
    }

    public static ComplexFraction operator +(ComplexFraction x)
    {
        return x;
    }

    public static Fraction Hypot(Fraction x1, Fraction y1)
    {
        return Complex64.Hypot((double)x1, (double)y1);
    }

    public Fraction Abs()
    {
        return Hypot(_real, _imag);
    }

    public static implicit operator Complex64(ComplexFraction cf)
    {
        return new Complex64((double)cf._real, (double)cf._imag);
    }

    public static implicit operator ComplexFraction(Complex64 cf)
    {
        return new ComplexFraction(cf.Real, cf.Imag);
    }


    public ComplexFraction Power(ComplexFraction y)
    {
        return ((Complex64)this).Power(y);
    }

    public override int GetHashCode()
    {
        return _real.GetHashCode() ^ _imag.GetHashCode();
    }

    public override bool Equals(object obj)
    {
        if (!(obj is ComplexFraction)) return false;
        return this == ((ComplexFraction)obj);
    }
}
