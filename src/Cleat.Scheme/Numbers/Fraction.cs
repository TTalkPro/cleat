#region License
// Fraction numbers
// (C) 2004 Andreas Tönne, All right reserved
// No GPL, no license fee!
// Anyone can use this source provided that this copyright stays in place

// Version 1.1 Bugfix release
// - new limit properties MaxValue etc.
// - checked for these limits in the constructor (BigInteger.MinValue slipped through)
// - CompareTo: equality corrected (a case was missing)
// - CompareTo: inqueality of very large fractions lead to OverflowExceptions
// -			added 128bit comparison in these cases only

/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// An implementation of rational (fractional) numbers.
/// Numeric range: -BigInteger.MaxValue/1 to BigInteger.MaxValue/1
/// Smallest positive number: 1/BigInteger.MaxValue
/// Smallest negative number: -1/BigInteger.MaxValue
/// Zero: 0/1
/// One: 1/1
/// 
/// Fractional numbers exhibit no rounding errors as BigInteger as they stay 
/// in their numeric range. In other cases a System.OverflowException is
/// raised (coming from the underlying BigInteger computation).
/// Note that overflow exceptions can be raised even if the reduced result
/// fraction is inside the numeric range.
/// 
/// The typeCode of Fraction is Decimal since this is the closest numeric
/// representation. Note however that conversion to and from Decimal will
/// loose numeric precision:
/// - going from Fraction 1/3 to Decimal is limited by the number of digits
///		representable by a Decimal (28)
///	- going from Decimal 1m/3m to Fraction will introduce rounding errors in
///		the resulting Fraction because Fraction is constructed from a Decimal as
///		sign * (int / scale) where the int is unsigned 96bit and scale ranges from
///		1 to 10^28. We need to reduce the int to signed 64bit and limit the scale
///		to 10^18. This results in rounding errors in the scale number as well
///		as lost insignificant digits in the int. 
///	However it is guaranteed that the conversion is true if the Decimal uses 18 or
///	less significant digits. 
/// </summary>
/// <remarks>
/// A fraction is kept normalized in the following way:
/// - the denominator is always positive
/// - the fraction is always reduced by the gcd of the nominator and denominator
/// </remarks>
[Serializable]
public class Fraction : IComparable, IConvertible
{
    #region Declarations

    private BigInteger _numerator;
    private BigInteger _denominator;

    #endregion

    #region Constructors

    public Fraction(BigInteger newNumerator, BigInteger newDenominator)
    {
        BigInteger gcd;

        if (newDenominator == 0)
            throw new DivideByZeroException("Illegal fraction");

        _numerator = newNumerator;
        _denominator = newDenominator;
        if (_denominator < 0)
        {
            _numerator *= -1;
            _denominator *= -1;
        }

        gcd = Gcd(_numerator, _denominator);
        _numerator /= gcd;
        _denominator /= gcd;
    }

    #endregion

    #region Properties

    public BigInteger Numerator
    {
        get
        {
            return _numerator;
        }
    }

    public BigInteger Denominator
    {
        get
        {
            return _denominator;
        }
    }

    public readonly static Fraction Zero = new Fraction(0, 1);

    #endregion

    #region GCD

    /// <summary>最大公约数（非负）。</summary>
    internal static BigInteger Gcd(BigInteger a, BigInteger b) => BigInteger.GreatestCommonDivisor(a, b);

    #endregion

    #region Conversions

    public double ToDouble(IFormatProvider foo)
    {
        return (double)this;
    }

    public static explicit operator double(Fraction fraction)
    {
        return BigIntegers.RationalToDouble(fraction._numerator, fraction._denominator);
    }

    public static explicit operator BigInteger(Fraction fraction)
    {
        return (BigInteger)fraction._numerator / (BigInteger)fraction._denominator;
    }

    public static implicit operator Fraction(int number)
    {
        return new Fraction(number, 1);
    }

    public static implicit operator Fraction(BigInteger number)
    {
        return new Fraction(number, 1);
    }

    public static implicit operator Fraction(double x)
    {
        if (x == 0.0)
        {
            return Zero;
        }

        BigInteger num, den;
        x.GetComponents(out num, out den);
        return new Fraction(num, den);
    }

    #endregion

    #region IComparable Members

    /// <summary>
    /// Note: the comparison is turned (by algebraic transformation) into a comparison
    /// of the cross-product of the fractions. This might overflow the available numbers,
    /// in which case we use home-made 128bit multiplication. This is slow! 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public int CompareTo(object obj)
    {
        Fraction arg = (Fraction)obj;

        if ((_numerator == arg._numerator) && (_denominator == arg._denominator))
            return 0;

        bool thisNegative = _numerator < 0;
        bool argNegative = arg._numerator < 0;
        if (thisNegative != argNegative)
        {
            if (argNegative)
                return 1;
            else
                return -1;
        }

        if (_numerator * arg._denominator < _denominator * arg._numerator)
            return -1;
        else
            return 1;
    }

    #endregion

    #region Operators

    public static bool operator <(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) < 0;
    }

    public static bool operator >(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) > 0;
    }

    public static bool operator ==(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) == 0;
    }

    public static bool operator !=(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) != 0;
    }

    public override bool Equals(object fraction1)
    {
        if (fraction1 is Fraction)
        {
            return CompareTo((Fraction)fraction1) == 0;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return (int)(double)this;
    }

    public static bool operator <=(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) <= 0;
    }

    public static bool operator >=(Fraction fraction1, Fraction fraction2)
    {
        return fraction1.CompareTo(fraction2) >= 0;
    }

    // **********************************************************************************

    public static Fraction operator +(Fraction fraction1, Fraction fraction2)
    {
        return new Fraction(
            (fraction1._numerator * fraction2._denominator) + (fraction2._numerator * fraction1._denominator),
            fraction1._denominator * fraction2._denominator);
    }

    public static Fraction operator -(Fraction fraction1, Fraction fraction2)
    {
        return new Fraction(
            (fraction1._numerator * fraction2._denominator) - (fraction2._numerator * fraction1._denominator),
            fraction1._denominator * fraction2._denominator);
    }

    public static Fraction operator /(Fraction fraction1, Fraction fraction2)
    {
        return new Fraction(
            fraction1._numerator * fraction2._denominator,
            fraction1._denominator * fraction2._numerator);
    }

    public static Fraction operator *(Fraction fraction1, Fraction fraction2)
    {
        return new Fraction(
            fraction1._numerator * fraction2._numerator,
            fraction1._denominator * fraction2._denominator);
    }

    public static Fraction operator %(Fraction fraction1, Fraction fraction2)
    {
        BigInteger quo = (BigInteger)(fraction1 / fraction2);
        return fraction1 - new Fraction(
            fraction2._numerator * quo,
            fraction2._denominator);
    }

    #endregion

    #region Alternative Operations

    public static Fraction Add(Fraction fraction1, Fraction fraction2)
    {
        return fraction1 + fraction2;
    }

    public static Fraction Subtract(Fraction fraction1, Fraction fraction2)
    {
        return fraction1 - fraction2;
    }

    public static Fraction Multiply(Fraction fraction1, Fraction fraction2)
    {
        return fraction1 * fraction2;
    }

    public static Fraction Divide(Fraction fraction1, Fraction fraction2)
    {
        return fraction1 / fraction2;
    }

    public static Fraction Modulus(Fraction fraction1, Fraction fraction2)
    {
        return fraction1 % fraction2;
    }

    #endregion

    public Fraction Abs()
    {
        if (this < 0)
        {
            return 0 - this;
        }
        else
        {
            return this;
        }
    }

    public override string ToString()
    {
        return BigIntegers.ToDecimalString(_numerator) + "/" + BigIntegers.ToDecimalString(_denominator);
    }

    #region IConvertible Members

    TypeCode IConvertible.GetTypeCode()
    {
        throw new NotImplementedException();
    }

    bool IConvertible.ToBoolean(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    byte IConvertible.ToByte(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    char IConvertible.ToChar(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    DateTime IConvertible.ToDateTime(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    decimal IConvertible.ToDecimal(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    double IConvertible.ToDouble(IFormatProvider provider)
    {
        return (double)this;
    }

    short IConvertible.ToInt16(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    int IConvertible.ToInt32(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    long IConvertible.ToInt64(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    sbyte IConvertible.ToSByte(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    float IConvertible.ToSingle(IFormatProvider provider)
    {
        return (float)(double)this;
    }

    string IConvertible.ToString(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    object IConvertible.ToType(Type conversionType, IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    ushort IConvertible.ToUInt16(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    uint IConvertible.ToUInt32(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    ulong IConvertible.ToUInt64(IFormatProvider provider)
    {
        throw new NotImplementedException();
    }

    #endregion
}
